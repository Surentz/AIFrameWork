using System.Net;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.ExternalSystems.Certificates;
using AiFramework.Infrastructure.ExternalSystems.Http;
using AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

/// <summary>
/// Real TLS against the simulator. The simulator trusts only the ROOT, and every client leaf is
/// issued by the INTERMEDIATE — so a success here means the handler sent the chain. On Windows the
/// machine store can paper over a missing intermediate; CI's Linux run is the real evidence.
/// </summary>
public sealed class ExternalSystemHandlerFactoryTests : IAsyncLifetime, IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-mtls-").FullName;
    private readonly TestPki _pki = TestPki.Create();
    private PartnerSimulatorApp _simulator = null!; // set in InitializeAsync, before any test runs.

    public async Task InitializeAsync() => _simulator = await PartnerSimulatorTests.StartAsync(_pki);

    public async Task DisposeAsync() => await _simulator.DisposeAsync();

    public void Dispose()
    {
        _pki.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private ExternalSystemHandlerFactory Factory(Action<ExternalSystemOptions> configure)
    {
        File.WriteAllText(Path.Combine(_directory, "ca.pem"), _pki.RootPem);
        var system = new ExternalSystemOptions
        {
            BaseAddress = _simulator.BaseAddress.ToString(),
            ServerTrust = new ServerTrustOptions
            {
                CaBundlePath = Path.Combine(_directory, "ca.pem"),
                CheckRevocation = false,
            },
        };
        configure(system);
        var options = new ExternalSystemsOptions();
        options.Systems["Sim"] = system;
        var monitor = new StaticOptionsMonitor(options);
        return new ExternalSystemHandlerFactory(
            new FileCertificateProvider(monitor, TimeProvider.System, NullLogger<FileCertificateProvider>.Instance),
            monitor,
            TimeProvider.System);
    }

    private ClientCertificateOptions WriteClientPfx(TestPki issuer, DateTimeOffset? notAfter = null)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, issuer.ExportPfx(issuer.IssueClient("aiframework", notAfter), password: null));
        return new ClientCertificateOptions { Path = path };
    }

    private async Task<HttpResponseMessage> PingAsync(ExternalSystemHandlerFactory factory)
    {
        using var client = new HttpClient(factory.CreatePrimaryHandler("Sim"));
        return await client.GetAsync(new Uri(_simulator.BaseAddress, "ping"));
    }

    [Fact]
    public async Task Send_WithALeafIssuedByTheIntermediate_Succeeds()
    {
        var factory = Factory(s => s.ClientCertificate = WriteClientPfx(_pki));

        var response = await PingAsync(factory);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public Task Send_WithoutAClientCertificate_FailsTheHandshake()
    {
        var factory = Factory(_ => { });

        var act = () => PingAsync(factory);

        return act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Send_WithACertificateFromAnotherPki_FailsTheHandshake()
    {
        using var stranger = TestPki.Create("Stranger");
        var factory = Factory(s => s.ClientCertificate = WriteClientPfx(stranger));

        var act = () => PingAsync(factory);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public Task Send_WithoutServerTrustForATestCa_RejectsTheServer()
    {
        var factory = Factory(s =>
        {
            s.ClientCertificate = WriteClientPfx(_pki);
            s.ServerTrust = null;
        });

        var act = () => PingAsync(factory);

        return act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Send_WhenTheCertificateFileIsMissing_FailsFastWithoutTheNetwork()
    {
        var factory = Factory(s => s.ClientCertificate = new ClientCertificateOptions
        {
            Path = Path.Combine(_directory, "absent.pfx"),
        });

        var act = () => PingAsync(factory);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.HttpRequestError.Should().Be(HttpRequestError.SecureConnectionError);
        _simulator.PingRequests.Should().Be(0);
    }

    [Fact]
    public async Task Send_WhenTheCertificateHasExpired_FailsFastWithoutTheNetwork()
    {
        var factory = Factory(s => s.ClientCertificate = WriteClientPfx(_pki, DateTimeOffset.UtcNow.AddHours(-1)));

        var act = () => PingAsync(factory);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .WithMessage("*certificate expired*");
        _simulator.PingRequests.Should().Be(0);
    }

    [Fact]
    public async Task Send_WhenTheTrustBundleHoldsNoCertificates_FailsFastWithoutTheNetwork()
    {
        var factory = Factory(s =>
        {
            s.ClientCertificate = WriteClientPfx(_pki);
            File.WriteAllText(s.ServerTrust!.CaBundlePath, "# no certificates here"); // Factory always sets ServerTrust.
        });

        var act = () => PingAsync(factory);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .WithMessage("*contains no certificates*");
        _simulator.PingRequests.Should().Be(0);
    }

    [Fact]
    public async Task Send_ForAnUnconfiguredSystem_FailsFast()
    {
        var factory = Factory(_ => { });
        using var client = new HttpClient(factory.CreatePrimaryHandler("NotConfigured"));

        var act = () => client.GetAsync(new Uri(_simulator.BaseAddress, "ping"));

        (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("*not configured*");
    }
}
