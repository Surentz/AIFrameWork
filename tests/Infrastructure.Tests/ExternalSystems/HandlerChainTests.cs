using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.ExternalSystems.Http;
using AiFramework.Infrastructure.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using NSubstitute;
using Refit;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class HandlerChainTests
{
    [Fact]
    public void AddClient_BuildsTheChainInTheFixedOrder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ITrafficRecorder>());
        services.AddResilience();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:Sim:BaseAddress"] = "https://partner.example/",
            ["Systems:Sim:Auth:Kind"] = "ClientSecret",
            ["Systems:Sim:Auth:TokenEndpoint"] = "https://idp.example/token",
            ["Systems:Sim:Auth:ClientId"] = "client",
            ["Systems:Sim:Auth:ClientSecretFile"] = "absent",
        })).AddClient<ISimulatorApi>("Sim");
        using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(UniqueName.ForType<ISimulatorApi>());

        Describe(handler).Should().ContainInOrder(
            "Traffic:Outbound", nameof(ResilienceHandler), "Traffic:OutboundAttempt", "Token", nameof(SocketsHttpHandler));
    }

    private static List<string> Describe(HttpMessageHandler handler)
    {
        var chain = new List<string>();
        for (HttpMessageHandler? current = handler; current is not null;
             current = (current as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(current switch
            {
                OutboundTrafficHandler traffic => $"Traffic:{traffic.Kind}",
                ResilienceHandler => nameof(ResilienceHandler),
                SocketsHttpHandler => nameof(SocketsHttpHandler),
                _ when current.GetType().Name.Contains("Token", StringComparison.Ordinal) => "Token",
                _ => current.GetType().Name,
            });
        }

        return chain;
    }
}
