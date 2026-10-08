using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using AiFramework.PartnerSimulator;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

/// <summary>
/// Proves the simulator itself, with a hand-built client, before anything in src/ relies on it:
/// later tests read "rejected" as our handler's fault, which is only true if the simulator
/// accepts a correct client and rejects a wrong one.
/// </summary>
public sealed class PartnerSimulatorTests
{
    [Fact]
    public async Task Ping_WithAClientCertificateFromTheTrustedPki_Returns200()
    {
        using var pki = TestPki.Create();
        await using var simulator = await StartAsync(pki);
        using var client = ClientPresenting(pki, TestPki.Usable(pki.IssueClient("client")));

        var response = await client.GetAsync(new Uri(simulator.BaseAddress, "ping"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ping_WithoutAClientCertificate_FailsTheHandshake()
    {
        using var pki = TestPki.Create();
        await using var simulator = await StartAsync(pki);
        using var client = ClientPresenting(pki, certificate: null);

        var act = () => client.GetAsync(new Uri(simulator.BaseAddress, "ping"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Ping_WithAClientCertificateFromAnotherPki_FailsTheHandshake()
    {
        using var pki = TestPki.Create();
        using var stranger = TestPki.Create("Stranger");
        await using var simulator = await StartAsync(pki);
        using var client = ClientPresenting(
            pki, TestPki.Usable(stranger.IssueClient("client")), stranger.Intermediate);

        var act = () => client.GetAsync(new Uri(simulator.BaseAddress, "ping"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Ping_WithALeafSentWithoutItsIntermediate_FailsTheHandshake()
    {
        using var pki = TestPki.Create();
        await using var simulator = await StartAsync(pki);
        using var client = ClientPresenting(pki, TestPki.Usable(pki.IssueClient("client")), intermediate: null);

        var act = () => client.GetAsync(new Uri(simulator.BaseAddress, "ping"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    internal static Task<PartnerSimulatorApp> StartAsync(TestPki pki, string? jwtAuthority = null) =>
        PartnerSimulatorApp.StartAsync(
            new PartnerSimulatorOptions
            {
                ServerCertificate = TestPki.Usable(pki.IssueServer()),
                TrustedClientRoot = pki.Root,
                JwtAuthority = jwtAuthority,
            },
            CancellationToken.None);

    private static HttpClient ClientPresenting(TestPki pki, X509Certificate2? certificate) =>
        ClientPresenting(pki, certificate, pki.Intermediate);

    /// <summary>Server trust always comes from <paramref name="pki"/>; <paramref name="intermediate"/> is what the client sends with its leaf.</summary>
    private static HttpClient ClientPresenting(
        TestPki pki, X509Certificate2? certificate, X509Certificate2? intermediate)
    {
        var ssl = new SslClientAuthenticationOptions();
        if (certificate is not null)
        {
            X509Certificate2[] sent = intermediate is null
                ? []
                : [X509CertificateLoader.LoadCertificate(intermediate.RawData)];
            ssl.ClientCertificateContext = SslStreamCertificateContext.Create(certificate, [.. sent], offline: true);
        }

        var trust = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        trust.CustomTrustStore.Add(pki.Root);
        ssl.CertificateChainPolicy = trust;

        // The HttpClient owns the handler and disposes it with itself; CA2000 cannot see that.
#pragma warning disable CA2000
        return new HttpClient(new SocketsHttpHandler { SslOptions = ssl });
#pragma warning restore CA2000
    }
}
