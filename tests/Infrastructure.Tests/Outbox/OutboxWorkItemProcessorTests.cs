using System.Diagnostics;
using AiFramework.Application.Abstractions;
using AiFramework.Domain.Abstractions;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using AiFramework.Infrastructure.Tests.Messaging;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Outbox;

public sealed record Boom : IDomainEvent;

public sealed class BoomHandler : IDomainEventHandler<Boom>
{
    public Task HandleAsync(Boom domainEvent, DomainEventContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("handler exploded");
}

// Amendment 1: a dedicated event/handler that captures the whole DomainEventContext, so the
// dedupe-key test can assert on MessageId directly instead of piggybacking on RecordingHandler's
// "text:attempt" string, which has no message id in it at all.
public sealed record Contextual : IDomainEvent;

public sealed class ContextCapturingHandler(ContextSink sink) : IDomainEventHandler<Contextual>
{
    public Task HandleAsync(Contextual domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        sink.Seen.Add(context);
        // Records the handler INSTANCE dispatch actually ran on, not just that some handler ran.
        // This is what lets a test compare "the instance the processor dispatched to" against
        // "the instance resolved from a given scope" — a same-count or non-empty check on Seen
        // alone cannot distinguish the processor's scope from any other scope, or from root.
        sink.Handlers.Add(this);
        // The ambient Activity a REAL handler would see — this is what
        // ProcessAsync_WithAStoredTraceParent_RestoresItAsTheDeliveryActivitysParent asserts on,
        // to prove trace continuity from inside dispatch itself rather than from the Activity
        // OutboxWorkItemProcessor started, which would pass even if the parent context were
        // wired to the wrong place.
        sink.ActivityContexts.Add(Activity.Current?.Context ?? default);
        return Task.CompletedTask;
    }
}

/// <summary>Singleton so it survives across the scopes each test resolves the processor through.</summary>
public sealed class ContextSink
{
    public ICollection<DomainEventContext> Seen { get; } = [];

    public ICollection<ContextCapturingHandler> Handlers { get; } = [];

    public ICollection<ActivityContext> ActivityContexts { get; } = [];
}

[Collection(nameof(PostgresCollection))]
public sealed class OutboxWorkItemProcessorTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    // Amendment 2: ValidateScopes = true so a scoped-from-root resolution throws instead of
    // silently succeeding. Every test below resolves the processor through a fresh
    // IServiceScope, never from the root provider directly, so this is exercised for real.
    private ServiceProvider BuildProvider(OutboxOptions? options = null, CapturingLoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        // OutboxWorkItemProcessor now resolves ILogger<OutboxWorkItemProcessor> via its primary
        // constructor, like every other consumer of ILogger<T> in this project's Build helpers.
        // SetMinimumLevel(Debug) only when a capturing provider is actually attached: it is what
        // Behaviors.LoggedAsync's own tests needed to observe the Debug-level "dispatched"
        // record, since Microsoft.Extensions.Logging defaults its filter to Information — see
        // LoggingBehaviorTests.Build for the fuller explanation.
        services.AddLogging(builder =>
        {
            if (logs is not null)
            {
                builder.SetMinimumLevel(LogLevel.Debug);
                builder.AddProvider(logs);
            }
        });
        services.AddSingleton<HandlerSink>();
        services.AddSingleton<ContextSink>();
        services.AddDomainEvent<Pinged>("test.pinged");
        services.AddDomainEvent<Boom>("test.boom");
        services.AddDomainEvent<Contextual>("test.contextual");
        services.AddScoped<IDomainEventHandler<Pinged>, RecordingHandler>();
        services.AddScoped<IDomainEventHandler<Boom>, BoomHandler>();
        services.AddScoped<IDomainEventHandler<Contextual>, ContextCapturingHandler>();
        services.AddSingleton<DomainEventRegistry>();
        services.AddSingleton<IClock>(new TestClock(Now));
        services.AddSingleton(Options.Create(options ?? new OutboxOptions()));
        services.AddDbContext<AiFrameworkDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddScoped<OutboxWorkItemProcessor>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private async Task<Guid> SeedAsync(string eventName, string payload, int attempts)
    {
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        context.Outbox.Add(new OutboxMessage
        {
            Id = id, EventName = eventName, Payload = payload, OccurredAt = Now,
            Status = OutboxStatus.InFlight, Attempts = attempts,
        });
        await context.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ProcessAsync_WhenTheHandlerSucceeds_MarksTheRowProcessed()
    {
        var id = await SeedAsync("test.pinged", """{"text":"hi"}""", 1);
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.pinged", """{"text":"hi"}""", 1), CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var row = await verify.Outbox.SingleAsync(m => m.Id == id);
        row.Status.Should().Be(OutboxStatus.Processed);
        row.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_WhenTheHandlerThrows_ReschedulesWithBackoff()
    {
        var id = await SeedAsync("test.boom", "{}", 1);
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.boom", "{}", 1), CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var row = await verify.Outbox.SingleAsync(m => m.Id == id);
        row.Status.Should().Be(OutboxStatus.Pending);
        row.NextAttemptAt.Should().BeAfter(Now);
        row.LastError.Should().Contain("handler exploded");
    }

    [Fact]
    public async Task ProcessAsync_OnTheFinalAttempt_MarksTheRowDead()
    {
        var id = await SeedAsync("test.boom", "{}", 5);
        await using var provider = BuildProvider(new OutboxOptions { MaxAttempts = 5 });
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.boom", "{}", 5), CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var row = await verify.Outbox.SingleAsync(m => m.Id == id);
        row.Status.Should().Be(OutboxStatus.Dead);
        row.LastError.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_WhenTheHandlerSucceeds_LogsDispatchedAtDebug()
    {
        var id = await SeedAsync("test.pinged", """{"text":"hi"}""", 1);
        using var logs = new CapturingLoggerProvider();
        await using var provider = BuildProvider(logs: logs);
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.pinged", """{"text":"hi"}""", 1), CancellationToken.None);

        logs.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Debug
            && r.Message.Contains("dispatched", StringComparison.Ordinal)
            && r.Message.Contains(id.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_WhenTheHandlerThrowsAndWillRetry_LogsRetryScheduledAtInformation()
    {
        var id = await SeedAsync("test.boom", "{}", 1);
        using var logs = new CapturingLoggerProvider();
        await using var provider = BuildProvider(logs: logs);
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.boom", "{}", 1), CancellationToken.None);

        logs.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Information
            && r.Message.Contains("retrying", StringComparison.Ordinal)
            && r.Message.Contains("handler exploded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_OnTheFinalAttempt_LogsDeadLetteredAtWarning()
    {
        var id = await SeedAsync("test.boom", "{}", 5);
        using var logs = new CapturingLoggerProvider();
        await using var provider = BuildProvider(new OutboxOptions { MaxAttempts = 5 }, logs);
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.boom", "{}", 5), CancellationToken.None);

        logs.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Warning
            && r.Message.Contains("dead-lettered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_WithAnUnknownEventName_MarksTheRowDeadImmediately()
    {
        var id = await SeedAsync("test.unregistered", "{}", 1);
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.unregistered", "{}", 1), CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var row = await verify.Outbox.SingleAsync(m => m.Id == id);
        row.Status.Should().Be(OutboxStatus.Dead, "no number of retries invents a registration");
    }

    [Fact]
    public async Task ProcessAsync_WithAnUnknownEventName_LogsDeadLetteredAtWarning()
    {
        var id = await SeedAsync("test.unregistered", "{}", 1);
        using var logs = new CapturingLoggerProvider();
        await using var provider = BuildProvider(logs: logs);
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.unregistered", "{}", 1), CancellationToken.None);

        logs.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Warning
            && r.Message.Contains("dead-lettered", StringComparison.Ordinal)
            && r.Message.Contains("No registration", StringComparison.Ordinal));
    }

    // Amendment 1: renamed from "...PassesTheMessageIdAsTheDedupeKey" and rewritten to assert
    // against a dedicated handler that captures the whole DomainEventContext, because the
    // original body asserted on RecordingHandler's "text:attempt" string — which contains no
    // message id — so it would have passed even if the processor passed Guid.Empty.
    [Fact]
    public async Task ProcessAsync_PassesTheSeededRowIdAndAttemptInTheDomainEventContext()
    {
        var id = await SeedAsync("test.contextual", "{}", 3);
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.contextual", "{}", 3), CancellationToken.None);

        var seen = provider.GetRequiredService<ContextSink>().Seen.Should().ContainSingle().Which;
        seen.MessageId.Should().Be(id);
        seen.Attempt.Should().Be(3);
    }

    // An integration event's occurredAt comes from here, so it must be the row's time, carried
    // through unchanged - not the clock at delivery.
    [Fact]
    public async Task ProcessAsync_PassesTheItemsOccurredAtInTheDomainEventContext()
    {
        var id = await SeedAsync("test.contextual", "{}", 1);
        var occurredAt = Now.AddHours(-2);
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(
                new OutboxWorkItem(id, "test.contextual", "{}", 1, OccurredAt: occurredAt), CancellationToken.None);

        provider.GetRequiredService<ContextSink>().Seen.Should().ContainSingle()
            .Which.OccurredAt.Should().Be(occurredAt);
    }

    // Amendment 2: proves the processor's injected IServiceProvider actually IS the scope it
    // was resolved from — Task 9's pump depends on this, so it is verified here rather than
    // assumed. This compares the HANDLER INSTANCE dispatch actually ran on (captured by
    // ContextCapturingHandler into ContextSink.Handlers) against the instance resolved directly
    // from the same scope. A scoped registration caches per scope regardless of what dispatch
    // did internally, so resolving the same service twice from one scope proves nothing on its
    // own — see the review round-1 note on the original version of this test, which did exactly
    // that and could not have failed even if dispatch had gone through root or a different
    // scope. Capturing "this" inside the handler and comparing it to a scope-resolved instance
    // closes that gap: it fails if dispatch resolved from anywhere other than this scope.
    [Fact]
    public async Task ProcessAsync_DispatchesToAHandlerResolvedFromTheCallersScope()
    {
        var id = await SeedAsync("test.contextual", "{}", 1);
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var expectedHandler = scope.ServiceProvider.GetRequiredService<IDomainEventHandler<Contextual>>();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.contextual", "{}", 1), CancellationToken.None);

        provider.GetRequiredService<ContextSink>().Handlers
            .Should().ContainSingle().Which.Should().BeSameAs(expectedHandler);
    }

    /// <summary>
    /// Task 5 (docs/superpowers/plans/2026-09-13-centralized-logging.md): proves trace
    /// continuity from INSIDE dispatch, via ContextCapturingHandler's own ambient
    /// Activity.Current — not by inspecting the Activity OutboxWorkItemProcessor started, which
    /// would pass even if that Activity's parent context were wired to the wrong value. An
    /// ActivityListener is required for StartActivity to produce anything at all: with no
    /// listener subscribed to "AiFramework.Outbox" (there is none in this bare ServiceProvider —
    /// no OpenTelemetry SDK here, unlike the real host), it always returns null regardless of
    /// what item.TraceParent holds.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_WithAStoredTraceParent_RestoresItAsTheDeliveryActivitysParent()
    {
        var expectedTraceId = ActivityTraceId.CreateRandom();
        var traceParent = $"00-{expectedTraceId}-{ActivitySpanId.CreateRandom()}-01";

        var id = await SeedAsync("test.contextual", "{}", 1);
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, "AiFramework.Outbox", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(
                new OutboxWorkItem(id, "test.contextual", "{}", 1, traceParent), CancellationToken.None);

        provider.GetRequiredService<ContextSink>().ActivityContexts
            .Should().ContainSingle().Which.TraceId.Should().Be(expectedTraceId,
                "the handler's own ambient Activity.Current must carry the SAME TraceId as the " +
                "request that raised the event, not a fresh unrelated one — that is the whole " +
                "point of restoring the stored traceparent as the delivery Activity's parent");
    }
}
