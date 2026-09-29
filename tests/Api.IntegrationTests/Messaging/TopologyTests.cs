using AiFramework.Infrastructure.EventPath;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace AiFramework.Api.IntegrationTests.Messaging;

/// <summary>What the API declares on the broker as it starts. ADR 0026.</summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class TopologyTests(ApiFactory factory)
{
    [Theory]
    [InlineData(RabbitMqTopology.EventsExchange)]
    [InlineData(RabbitMqTopology.UnroutedExchange)]
    public async Task Startup_DeclaresTheExchange(string exchange)
    {
        _ = factory.Services; // builds and starts the host, which provisions the topology
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);

        var act = () => probe.ExchangeExistsAsync(exchange);

        await act.Should().NotThrowAsync($"{exchange} is declared by AutoProvision at startup");
    }

    [Fact]
    public async Task Startup_DeclaresTheUnroutedQueue()
    {
        _ = factory.Services;
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);

        var act = () => probe.MessageCountAsync(RabbitMqTopology.UnroutedQueue);

        await act.Should().NotThrowAsync();
    }

    /// <remarks>
    /// Reads the runtime's ACTIVE listeners, not <c>ServiceCapabilities.MessagingEndpoints</c>:
    /// capabilities do not list the <c>wolverine.response.&lt;guid&gt;</c> reply listener Wolverine
    /// starts on every RabbitMQ host, even a sender-only one, so a capabilities-based version of
    /// this test passed while the API was in fact consuming from the broker.
    /// </remarks>
    [Fact]
    public void TheApi_ListensOnNoRabbitMqQueue()
    {
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();

        var rabbitListeners = runtime.Endpoints.ActiveListeners()
            .Where(listener => string.Equals(listener.Uri.Scheme, "rabbitmq", StringComparison.Ordinal))
            .Select(listener => listener.Uri.ToString())
            .ToList();

        rabbitListeners.Should().BeEmpty("the API listens to no queue (ADR 0016); only the worker does");
    }

    [Fact]
    public void Startup_WithoutARabbitMqConnectionString_Refuses()
    {
        using var misconfigured = factory.WithWebHostBuilder(
            builder => builder.UseSetting("ConnectionStrings:RabbitMq", string.Empty));

        var act = () => misconfigured.Services;

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionStrings:RabbitMq*");
    }
}
