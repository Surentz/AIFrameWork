using System.Threading.Channels;
using AiFramework.Infrastructure.Outbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Outbox;

/// <summary>
/// Pure DI checks for AddOutbox - no database, no Docker. AddInfrastructure calls
/// AddOutbox, but these resolve AddOutbox in isolation, backed only by AddLogging(): none of
/// the assertions below construct an OutboxPoller or OutboxWorkItemProcessor (both need a real
/// AiFrameworkDbContext), only the channel plumbing and the two BackgroundServices themselves,
/// which resolve their scoped dependencies lazily inside ExecuteAsync rather than through
/// their own constructors.
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
    public void AddOutbox_RegistersOutboxPollerServiceAsAHostedServiceThatResolves()
    {
        using var provider = BuildProvider();

        var hostedServices = provider.GetServices<IHostedService>();

        hostedServices.Should().ContainSingle(s => s is OutboxPollerService);
    }

    [Fact]
    public void AddOutbox_RegistersOutboxWorkerServiceAsAHostedServiceThatResolves()
    {
        using var provider = BuildProvider();

        var hostedServices = provider.GetServices<IHostedService>();

        hostedServices.Should().ContainSingle(s => s is OutboxWorkerService);
    }
}
