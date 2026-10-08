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
    // IHttpClientFactory's own wrappers: lifetime tracking and its two logging handlers. Not ours.
    private static readonly HashSet<string> FactoryPlumbing = new(StringComparer.Ordinal)
    {
        "LifetimeTrackingHttpMessageHandler", "LoggingScopeHttpMessageHandler", "LoggingHttpMessageHandler",
    };

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

        // Exact, so a missing, extra or moved layer fails. ResilienceHandler exposes no pipeline
        // name, so the two are told apart by position: #1 is the standard handler, #2 the 401
        // resend. Swapping those two is caught by behaviour instead: ExternalSystemClientTests'
        // 503 test counts the attempts that only the standard handler, outside OutboundAttempt,
        // produces.
        Describe(handler).Should().Equal(
            "Traffic:Outbound", "ResilienceHandler#1", "Traffic:OutboundAttempt", "ResilienceHandler#2", "Token",
            nameof(SocketsHttpHandler));
    }

    private static List<string> Describe(HttpMessageHandler handler)
    {
        var chain = new List<string>();
        var resilience = 0;
        for (HttpMessageHandler? current = handler; current is not null;
             current = (current as DelegatingHandler)?.InnerHandler)
        {
            if (FactoryPlumbing.Contains(current.GetType().Name))
            {
                continue;
            }

            chain.Add(current switch
            {
                OutboundTrafficHandler traffic => $"Traffic:{traffic.Kind}",
                ResilienceHandler => $"{nameof(ResilienceHandler)}#{++resilience}",
                SocketsHttpHandler => nameof(SocketsHttpHandler),
                _ when current.GetType().Name.Contains("Token", StringComparison.Ordinal) => "Token",
                _ => current.GetType().Name,
            });
        }

        return chain;
    }
}
