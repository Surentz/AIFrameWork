using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.ExternalSystems.Certificates;
using AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class FileCertificateProviderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-certs-").FullName;
    private readonly TestPki _pki = TestPki.Create();
    private readonly FakeTimeProvider _clock = new();

    public void Dispose()
    {
        _pki.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private FileCertificateProvider Provider(string? passwordFile = "client.pass")
    {
        var options = new ExternalSystemsOptions();
        options.Systems["Sim"] = new ExternalSystemOptions
        {
            BaseAddress = "https://localhost/",
            ClientCertificate = new ClientCertificateOptions
            {
                Path = Path.Combine(_directory, "client.pfx"),
                PasswordFile = passwordFile is null ? null : Path.Combine(_directory, passwordFile),
            },
        };
        return new FileCertificateProvider(
            new StaticOptionsMonitor(options), _clock, NullLogger<FileCertificateProvider>.Instance);
    }

    private void WritePfx(string commonName, string password = "secret")
    {
        File.WriteAllBytes(Path.Combine(_directory, "client.pfx"), _pki.ExportPfx(_pki.IssueClient(commonName), password));
        File.WriteAllText(Path.Combine(_directory, "client.pass"), password + "\n");
    }

    [Fact]
    public void GetCurrent_WithAValidPfx_LoadsTheLeafAndItsIntermediate()
    {
        WritePfx("first");

        var result = Provider().GetCurrent("Sim");

        result.Problem.Should().BeNull();
        result.Certificate!.Certificate.Subject.Should().Be("CN=first"); // Problem is null, so Certificate is set.
        result.Certificate.Context.IntermediateCertificates.Should().ContainSingle(
            c => c.Thumbprint == _pki.Intermediate.Thumbprint);
    }

    [Fact]
    public void GetCurrent_WhenTheFileIsMissing_ReportsAProblem()
    {
        var result = Provider().GetCurrent("Sim");

        result.Certificate.Should().BeNull();
        result.Problem.Should().Be("client certificate file not found");
    }

    [Fact]
    public void GetCurrent_WithAWrongPassword_ReportsAProblemWithoutThePath()
    {
        WritePfx("first");
        File.WriteAllText(Path.Combine(_directory, "client.pass"), "wrong");

        var result = Provider().GetCurrent("Sim");

        result.Certificate.Should().BeNull();
        result.Problem.Should().StartWith("client certificate could not be read").And.NotContain(_directory);
    }

    [Fact]
    public void GetCurrent_ForASystemWithoutACertificate_IsNotConfigured()
    {
        var result = Provider().GetCurrent("Unknown");

        result.Should().Be(CertificateLoadResult.NotConfigured);
    }

    [Fact]
    public void GetCurrent_AfterTheFileChangesWithinTheRecheckInterval_KeepsTheOldCertificate()
    {
        WritePfx("first");
        var provider = Provider();
        provider.GetCurrent("Sim");
        WritePfx("second");
        _clock.Advance(FileCertificateProvider.RecheckInterval - TimeSpan.FromSeconds(1));

        var result = provider.GetCurrent("Sim");

        result.Certificate!.Certificate.Subject.Should().Be("CN=first"); // loaded above.
    }

    [Fact]
    public void GetCurrent_AfterTheFileChangesAndTheRecheckIntervalPasses_LoadsTheNewCertificate()
    {
        WritePfx("first");
        var provider = Provider();
        provider.GetCurrent("Sim");
        WritePfx("second");
        _clock.Advance(FileCertificateProvider.RecheckInterval);

        var result = provider.GetCurrent("Sim");

        result.Certificate!.Certificate.Subject.Should().Be("CN=second"); // the new file is valid.
    }
}
