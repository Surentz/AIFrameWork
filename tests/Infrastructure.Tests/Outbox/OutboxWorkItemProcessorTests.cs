using AiFramework.Application.Abstractions;
using AiFramework.Domain.Abstractions;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        return Task.CompletedTask;
    }
}

/// <summary>Singleton so it survives across the scopes each test resolves the processor through.</summary>
public sealed class ContextSink
{
    public ICollection<DomainEventContext> Seen { get; } = [];
}

[Collection(nameof(PostgresCollection))]
public sealed class OutboxWorkItemProcessorTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    // Amendment 2: ValidateScopes = true so a scoped-from-root resolution throws instead of
    // silently succeeding. Every test below resolves the processor through a fresh
    // IServiceScope, never from the root provider directly, so this is exercised for real.
    private ServiceProvider BuildProvider(OutboxOptions? options = null)
    {
        var services = new ServiceCollection();
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

    // Amendment 2: proves the processor's injected IServiceProvider actually IS the scope it
    // was resolved from — Task 9's pump depends on this, so it is verified here rather than
    // assumed. A handler resolved from the scope should see the SAME scoped instance the test
    // resolves from that same scope; a distinct instance (or a handler resolved from root)
    // would mean Task 9's per-item scope buys nothing.
    [Fact]
    public async Task ProcessAsync_ResolvesHandlersFromTheScopeTheProcessorWasResolvedFrom()
    {
        var id = await SeedAsync("test.pinged", """{"text":"scope-check"}""", 1);
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var expectedSink = scope.ServiceProvider.GetRequiredService<IDomainEventHandler<Pinged>>();

        await scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>()
            .ProcessAsync(new OutboxWorkItem(id, "test.pinged", """{"text":"scope-check"}""", 1), CancellationToken.None);

        // RecordingHandler is scoped; resolving it again from the SAME scope must return the
        // identical instance if and only if the handler the processor dispatched to was also
        // resolved from this scope rather than from root.
        scope.ServiceProvider.GetRequiredService<IDomainEventHandler<Pinged>>().Should().BeSameAs(expectedSink);
    }
}
