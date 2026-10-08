using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.Resilience;
using AiFramework.Infrastructure.Tests.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Refit;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

/// <summary>
/// The token-resend pipeline against a stub partner and a stub token endpoint, so the number of
/// tokens fetched is exact. ExternalSystemClientTests covers the same pipeline against Keycloak.
/// </summary>
public sealed class TokenResendTests
{
    private readonly Queue<(HttpStatusCode Status, TimeSpan? RetryAfter)> _script = new();
    private readonly List<string?> _authorizations = [];
    private int _tokensIssued;

    private ServiceProvider Build(Action<ExternalSystemClientBuilder<ISimulatorApi>>? client = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ITrafficRecorder>());
        services.AddResilience();
        var builder = services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:Sim:BaseAddress"] = "https://partner.example/",
            ["Systems:Sim:Resilience:BaseDelay"] = "00:00:00.010",
            ["Systems:Sim:Auth:Kind"] = "ClientSecret",
            ["Systems:Sim:Auth:TokenEndpoint"] = "https://idp.example/token",
            ["Systems:Sim:Auth:ClientId"] = "client",
            ["Systems:Sim:Auth:ClientSecretFile"] = "absent",
        })).AddClient<ISimulatorApi>("Sim");
        client?.Invoke(builder);

        services.AddHttpClient(UniqueName.ForType<ISimulatorApi>()).ConfigurePrimaryHttpMessageHandler(() =>
            new StubHttpMessageHandler(request =>
            {
                lock (_authorizations)
                {
                    _authorizations.Add(request.Headers.Authorization?.ToString());
                    var (status, retryAfter) = _script.Count > 0 ? _script.Dequeue() : (HttpStatusCode.OK, null);
                    var response = new HttpResponseMessage(status) { RequestMessage = request };
                    if (retryAfter is { } delay)
                    {
                        response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
                    }

                    return response;
                }
            }));
        services.AddHttpClient(ExternalSystemNames.TokenBackchannel("Sim")).ConfigurePrimaryHttpMessageHandler(() =>
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"access_token":"token-{{Interlocked.Increment(ref _tokensIssued)}}","token_type":"Bearer","expires_in":300}""",
                    Encoding.UTF8,
                    "application/json"),
            }));
        return services.BuildServiceProvider();
    }

    // The resend's forced renewal must not leak into the standard retry's later attempts of the
    // same call: each would otherwise fetch a fresh token from the IdP.
    [Fact]
    public async Task Send_WhenARetryFollowsAResend_ReusesTheResendsToken()
    {
        await using var provider = Build();
        _script.Enqueue((HttpStatusCode.Unauthorized, null));
        _script.Enqueue((HttpStatusCode.ServiceUnavailable, null));

        var response = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _authorizations.Should().Equal("Bearer token-1", "Bearer token-2", "Bearer token-2");
    }

    // A Retry-After on the 401 must not delay the resend: the wait would sit inside AttemptTimeout
    // (3 s by default), and with retry off nothing outside rescues a timed-out attempt.
    [Fact]
    public async Task Send_WhenA401CarriesRetryAfter_ResendsWithoutWaiting()
    {
        await using var provider = Build(c => c.WithoutRetry("the test needs a single attempt"));
        _script.Enqueue((HttpStatusCode.Unauthorized, TimeSpan.FromSeconds(30)));

        var response = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
