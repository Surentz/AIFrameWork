using System.Threading.Channels;
using AiFramework.Infrastructure.Outbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Outbox;

/// <summary>
/// Pure DI checks for AddOutbox and AddOutboxPumps - no database, no Docker. AddInfrastructure
/// calls AddOutbox and only the Api calls AddOutboxPumps, but these resolve both in isolation,
/// backed only by AddLogging(): no test
/// constructs a real OutboxPoller or OutboxWorkItemProcessor (both need a real
/// AiFrameworkDbContext). The channel plumbing and the two BackgroundServices resolve their
/// scoped dependencies lazily inside ExecuteAsync rather than through their own constructors,
/// which is what lets the critical-fix test below override OutboxWorkItemProcessor's own
/// registration with a throwing factory instead of a real one.
/// </summary>
public sealed class OutboxRegistrationTests
{
    // OutboxOptions' properties are init-only (see OutboxOptions.cs), so they cannot be
    // assigned from inside a services.Configure(Action of OutboxOptions) delegate - that is a
    // plain method body, not an object initializer or constructor, and the C# compiler rejects
    // it (CS8852). An explicit IOptions of OutboxOptions built from an object initializer,
    // registered AFTER AddOutbox() so it is the last (and therefore winning) registration for
    // that closed service type, configures options without fighting init-only properties.
    private static ServiceProvider BuildProvider(OutboxOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutbox();
        services.AddOutboxPumps();

        if (options is not null)
        {
            services.AddSingleton<IOptions<OutboxOptions>>(Options.Create(options));
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddOutbox_RegistersAWriterThatIsTheChannelsOwnWriter()
    {
        using var provider = BuildProvider();

        var channel = provider.GetRequiredService<Channel<OutboxWorkItem>>();
        var writer = provider.GetRequiredService<ChannelWriter<OutboxWorkItem>>();

        // Reference identity, not equivalence: if the writer resolved here were merely "a"
        // writer rather than THIS channel's own, the poller would write into a channel nobody
        // drains - silent, total failure of the feature.
        writer.Should().BeSameAs(channel.Writer);
    }

    [Fact]
    public void AddOutbox_RegistersAReaderThatIsTheChannelsOwnReader()
    {
        using var provider = BuildProvider();

        var channel = provider.GetRequiredService<Channel<OutboxWorkItem>>();
        var reader = provider.GetRequiredService<ChannelReader<OutboxWorkItem>>();

        reader.Should().BeSameAs(channel.Reader);
    }

    [Fact]
    public void AddOutbox_BuildsTheChannelFromConfiguredCapacityNotADefaultOutboxOptions()
    {
        using var provider = BuildProvider(new OutboxOptions { ChannelCapacity = 1 });
        var writer = provider.GetRequiredService<ChannelWriter<OutboxWorkItem>>();

        writer.TryWrite(new OutboxWorkItem(Guid.NewGuid(), "test.event", "{}", 0))
            .Should().BeTrue("the channel has room for its first item under a capacity of 1");

        // TryWrite gives an immediate, non-blocking answer instead of racing WriteAsync against
        // a timeout. If the channel had been built from a fresh OutboxOptions() rather than the
        // configured one, its capacity would be the default 100 and this second write would
        // also succeed - which is exactly the bug this test exists to catch.
        writer.TryWrite(new OutboxWorkItem(Guid.NewGuid(), "test.event", "{}", 0))
            .Should().BeFalse("ChannelCapacity was configured to 1 and the channel is already full");
    }

    [Fact]
    public void AddOutbox_Alone_StartsNoPump()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutbox();
        using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>();

        // AddInfrastructure calls AddOutbox in every host, and the pumps run every domain event
        // handler - the notifiers among them. A host with no push transport that polled the
        // outbox would write notifications nobody is told about. See AddOutboxPumps.
        hostedServices.Should().BeEmpty();
    }

    [Fact]
    public void AddOutboxPumps_RegistersOutboxPollerServiceAsAHostedServiceThatResolves()
    {
        using var provider = BuildProvider();

        var hostedServices = provider.GetServices<IHostedService>();

        hostedServices.Should().ContainSingle(s => s is OutboxPollerService);
    }

    [Fact]
    public void AddOutboxPumps_RegistersOutboxWorkerServiceAsAHostedServiceThatResolves()
    {
        using var provider = BuildProvider();

        var hostedServices = provider.GetServices<IHostedService>();

        hostedServices.Should().ContainSingle(s => s is OutboxWorkerService);
    }

    // ConfigurationBinder sets init-only properties through reflection (PropertyInfo.SetValue),
    // which the CLR allows on an init accessor - only the C# compiler enforces the init
    // restriction, and only at the call site. This IConfigureOptions mirrors that mechanism so
    // the test below goes through AddOutbox's real IOptionsFactory pipeline (unlike
    // BuildProvider's Options.Create bypass), without pulling in the
    // Microsoft.Extensions.Configuration.Binder package just for one test.
    private sealed class SetWorkerCountToZero : IConfigureOptions<OutboxOptions>
    {
        public void Configure(OutboxOptions options) =>
            typeof(OutboxOptions).GetProperty(nameof(OutboxOptions.WorkerCount))!.SetValue(options, 0);
    }

    [Fact]
    public void AddOutbox_WithAnInvalidWorkerCount_ThrowsWhenTheChannelIsResolved()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutbox();
        services.AddSingleton<IConfigureOptions<OutboxOptions>>(new SetWorkerCountToZero());

        using var provider = services.BuildServiceProvider();

        // The channel factory in AddOutbox resolves IOptions<OutboxOptions>.Value to read
        // ChannelCapacity. That is the same call a real host makes the first time anything
        // needs the channel, which is at host start: both hosted services take a
        // ChannelWriter/ChannelReader built from it, and the generic host resolves every
        // IHostedService as part of starting up. This proves that path is where an invalid
        // WorkerCount is actually caught - loudly, at startup - rather than left to silently
        // produce a zero-worker no-op once the host is already running.
        var act = () => provider.GetRequiredService<Channel<OutboxWorkItem>>();

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*WorkerCount*");
    }

    [Fact]
    public void AddOutbox_WithOptionsRegisteredViaOptionsCreate_BypassesValidation()
    {
        // Confirms the bypass the other tests in this file rely on via BuildProvider: unlike
        // AddOutbox_WithAnInvalidWorkerCount_ThrowsWhenTheChannelIsResolved above,
        // Options.Create wraps a value directly into an IOptions<T> with no IOptionsFactory
        // involved, so none of AddOutbox's .Validate(...) calls ever run against it - a real
        // host, which builds OutboxOptions through configuration binding, would still catch
        // this.
        using var provider = BuildProvider(new OutboxOptions { WorkerCount = 0 });

        // Resolving IHostedService constructs both pumps, and each stores options.Value in a
        // field initializer - the same access that throws in the test above when validation
        // actually runs.
        var act = () => provider.GetServices<IHostedService>().ToList();

        act.Should().NotThrow(
            "Options.Create bypasses IOptionsFactory entirely, so AddOutbox's WorkerCount " +
            "validation never runs for an options instance registered this way");
    }

    [Fact]
    public async Task OutboxWorkerService_WhenResolvingTheProcessorFails_LogsAndKeepsDrainingTheChannel()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutbox();
        services.AddOutboxPumps();

        var resolutionAttempts = 0;

        // Overrides AddOutbox's own AddScoped<OutboxWorkItemProcessor>() - the last
        // registration for a closed service type wins for GetRequiredService, as
        // AddOutbox_BuildsTheChannelFromConfiguredCapacityNotADefaultOutboxOptions's sibling
        // tests already rely on. This simulates a resolution failure (a broken registration, a
        // constructor that throws) without a database: DI throws before ProcessAsync is ever
        // reached, exercising exactly the gap the Critical fix in ProcessOneAsync closes.
        services.AddScoped<OutboxWorkItemProcessor>(_ =>
        {
            Interlocked.Increment(ref resolutionAttempts);
            throw new InvalidOperationException("Simulated resolution failure.");
        });

        using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<ChannelWriter<OutboxWorkItem>>();

        writer.TryWrite(new OutboxWorkItem(Guid.NewGuid(), "test.event", "{}", 0)).Should().BeTrue();
        writer.TryWrite(new OutboxWorkItem(Guid.NewGuid(), "test.event", "{}", 0)).Should().BeTrue();
        writer.Complete();

        var worker = provider.GetServices<IHostedService>().OfType<OutboxWorkerService>().Single();

        // ExecuteTask completes once the channel drains - writer.Complete() above ends
        // ReadAllAsync's enumeration once both buffered items are read, regardless of what
        // ProcessOneAsync does with them. Awaiting it, without it faulting, is what proves the
        // worker survived resolution throwing on BOTH items rather than dying after the first:
        // ExecuteAsync's Task.WhenAll(WorkerCount tasks) only completes successfully if every
        // worker's foreach loop ran to completion.
        Func<Task> act = async () =>
        {
            await worker.StartAsync(CancellationToken.None);
            await worker.ExecuteTask!;
        };

        await act.Should().NotThrowAsync(
            "a resolution failure inside ProcessOneAsync must be caught and logged, not left " +
            "to propagate out of RunAsync and fail Task.WhenAll");

        resolutionAttempts.Should().Be(2,
            "both items must reach resolution, not just the first, for this to prove the " +
            "worker keeps draining rather than dying after one failure");
    }
}
