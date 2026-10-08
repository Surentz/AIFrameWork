using System.Net;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.Resilience;
using AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Refit;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public interface ISimulatorApi
{
    [Get("/echo")]
    public Task<IApiResponse<EchoResponse>> EchoAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The whole chain, for real: Refit → traffic → resilience → traffic → Duende → mTLS, against the
/// simulator (trusting this run's Keycloak PKI) and a real Keycloak. Retry delays are real but
/// tiny (BaseDelay 50 ms; the 401 resend has none) rather than faked, because Duende's cache also
/// reads the clock and the two must agree.
/// </summary>
[Collection(nameof(KeycloakCollection))]
public sealed class ExternalSystemClientTests(KeycloakFixture keycloak) : IAsyncLifetime, IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-client-").FullName;
    private readonly ITrafficRecorder _traffic = Substitute.For<ITrafficRecorder>();
    private PartnerSimulatorApp _simulator = null!; // set in InitializeAsync.

    public async Task InitializeAsync() =>
        _simulator = await PartnerSimulatorTests.StartAsync(keycloak.Pki, jwtAuthority: keycloak.Issuer);

    public async Task DisposeAsync() => await _simulator.DisposeAsync();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ServiceProvider Build(string authKind = "ClientSecret", Action<ExternalSystemClientBuilder<ISimulatorApi>>? client = null,
        bool resilienceEnabled = true)
    {
        File.WriteAllText(Path.Combine(_directory, "ca.pem"), keycloak.Pki.RootPem);
        var config = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:Sim:BaseAddress"] = _simulator.BaseAddress.ToString(),
            ["Systems:Sim:Resilience:BaseDelay"] = "00:00:00.050",
            ["Systems:Sim:Resilience:MaxRetryAttempts"] = "2",
            ["Systems:Sim:ServerTrust:CaBundlePath"] = Path.Combine(_directory, "ca.pem"),
            ["Systems:Sim:ServerTrust:CheckRevocation"] = "false",
            ["Systems:Sim:ClientCertificate:Path"] = keycloak.WriteJwtClientPfx(_directory),
            ["Systems:Sim:Auth:Kind"] = authKind,
            ["Systems:Sim:Auth:TokenEndpoint"] = keycloak.TokenEndpoint,
            ["Systems:Sim:Auth:Issuer"] = keycloak.Issuer,
        };
        if (string.Equals(authKind, "ClientSecret", StringComparison.Ordinal))
        {
            config["Systems:Sim:Auth:ClientId"] = KeycloakFixture.SecretClientId;
            config["Systems:Sim:Auth:ClientSecretFile"] = KeycloakFixture.WriteSecretFile(_directory);
        }
        else
        {
            config["Systems:Sim:Auth:ClientId"] = KeycloakFixture.JwtClientId;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_traffic);
        services.AddResilience();
        services.Configure<ResilienceOptions>(o => o.Enabled = resilienceEnabled);
        var builder = services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(config))
            .AddClient<ISimulatorApi>("Sim");
        client?.Invoke(builder);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("ClientSecret")]
    [InlineData("PrivateKeyJwt")]
    public async Task Send_ThroughTheFullChain_ReachesThePartnerWithACertificateAndAToken(string authKind)
    {
        await using var provider = Build(authKind);

        var response = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        response.IsSuccessStatusCode.Should().BeTrue((response.Error as ApiException)?.Content ?? response.Error?.Message);
        response.Content!.ClientCertificateSubject.Should().Be($"CN={KeycloakFixture.JwtClientId}"); // success has content.
        _simulator.EchoRequests.Should().ContainSingle().Which.Authorization.Should().StartWith("Bearer ");
    }

    [Fact]
    public async Task Send_WhenThePartnerAnswers503Once_RetriesAndRecordsOneCallAndTwoAttempts()
    {
        await using var provider = Build();
        _simulator.EnqueueEchoStatus(HttpStatusCode.ServiceUnavailable);

        var response = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        response.IsSuccessStatusCode.Should().BeTrue();
        _traffic.Received(1).Record(TrafficKind.Outbound, "Sim", TrafficOutcome.Succeeded, Arg.Any<long>());
        _traffic.Received(1).Record(TrafficKind.OutboundAttempt, "Sim", TrafficOutcome.Faulted, Arg.Any<long>());
        _traffic.Received(1).Record(TrafficKind.OutboundAttempt, "Sim", TrafficOutcome.Succeeded, Arg.Any<long>());
    }

    // §5 probe 1.
    [Fact]
    public async Task Send_WhenThePartnerAnswers401Once_RefreshesTheTokenOnceAndSucceeds()
    {
        await using var provider = Build();
        var api = provider.GetRequiredService<ISimulatorApi>();
        await api.EchoAsync(CancellationToken.None); // warm the token cache with token A.
        _simulator.EnqueueEchoStatus(HttpStatusCode.Unauthorized);

        var response = await api.EchoAsync(CancellationToken.None);

        response.IsSuccessStatusCode.Should().BeTrue();
        var authorizations = _simulator.EchoRequests.Select(r => r.Authorization).ToList();
        authorizations.Should().HaveCount(3, "the warm-up, the 401, and exactly one resend");
        authorizations[2].Should().NotBe(authorizations[1], "the resend must carry a freshly fetched token");
    }

    // The 401 resend is not the standard retry: a non-idempotent client still gets it, because a
    // 401 means the partner did not act on the request.
    [Fact]
    public async Task Send_WithoutRetryWhenThePartnerAnswers401Once_StillRefreshesOnceAndSucceeds()
    {
        await using var provider = Build(client: c => c.WithoutRetry("CreateThing is not idempotent"));
        var api = provider.GetRequiredService<ISimulatorApi>();
        await api.EchoAsync(CancellationToken.None); // warm the token cache with token A.
        _simulator.EnqueueEchoStatus(HttpStatusCode.Unauthorized);

        var response = await api.EchoAsync(CancellationToken.None);

        response.IsSuccessStatusCode.Should().BeTrue();
        var authorizations = _simulator.EchoRequests.Select(r => r.Authorization).ToList();
        authorizations.Should().HaveCount(3, "the warm-up, the 401, and exactly one resend");
        authorizations[2].Should().NotBe(authorizations[1], "the resend must carry a freshly fetched token");
    }

    [Fact]
    public async Task Send_WithoutRetry_MakesExactlyOneAttemptOnA503()
    {
        await using var provider = Build(client: c => c.WithoutRetry("CreateThing is not idempotent"));
        _simulator.EnqueueEchoStatus(HttpStatusCode.ServiceUnavailable);

        var response = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _simulator.EchoRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Send_WithResilienceDisabledGlobally_MakesExactlyOneAttemptOnA503()
    {
        await using var provider = Build(resilienceEnabled: false);
        _simulator.EnqueueEchoStatus(HttpStatusCode.ServiceUnavailable);

        var response = await provider.GetRequiredService<ISimulatorApi>().EchoAsync(CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _simulator.EchoRequests.Should().ContainSingle();
    }

    [Fact]
    public void WithoutRetry_WithoutAReason_Throws()
    {
        var act = () => Build(client: c => c.WithoutRetry(" "));

        act.Should().Throw<ArgumentException>();
    }
}
