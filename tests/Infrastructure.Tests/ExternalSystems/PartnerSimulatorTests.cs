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

    internal static Task<PartnerSimulatorApp> StartAsync(TestPki pki, string? jwtAuthority = null) =>
        PartnerSimulatorApp.StartAsync(
            new PartnerSimulatorOptions
            {
                ServerCertificate = TestPki.Usable(pki.IssueServer()),
                TrustedClientRoot = pki.Root,
                JwtAuthority = jwtAuthority,
            },
            CancellationToken.None);

    private static HttpClient ClientPresenting(TestPki pki, X509Certificate2? certificate)
    {
        var ssl = new SslClientAuthenticationOptions();
        if (certificate is not null)
        {
            ssl.ClientCertificateContext = SslStreamCertificateContext.Create(
                certificate, [X509CertificateLoader.LoadCertificate(pki.Intermediate.RawData)], offline: true);
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
