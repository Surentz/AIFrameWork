using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.ExternalSystems.Health;
using AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class CertificateHealthCheckTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-cert-health-").FullName;
    private readonly TestPki _pki = TestPki.Create();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public void Dispose()
    {
        _pki.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string WritePfx(string name, DateTimeOffset notAfter)
    {
        var path = Path.Combine(_directory, $"{name}.pfx");
        File.WriteAllBytes(path, _pki.ExportPfx(_pki.IssueClient(name, notAfter), password: null));
        return path;
    }

    private async Task<HealthReport> CheckAsync(IDictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(config));
        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(c => c.Name.EndsWith(":certificate", StringComparison.Ordinal));
    }

    private static Dictionary<string, string?> System(string name, string? pfx)
    {
        var config = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"Systems:{name}:BaseAddress"] = "https://partner.example/",
        };

        // A null-valued key still makes the binder create a ClientCertificate object; an absent
        // key is what "no certificate" means in real configuration.
        if (pfx is not null)
        {
            config[$"Systems:{name}:ClientCertificate:Path"] = pfx;
        }

        return config;
    }

    [Fact]
    public async Task CheckHealth_WithACertificateFarFromExpiry_IsHealthy()
    {
        var report = await CheckAsync(System("Sim", WritePfx("Sim", _clock.GetUtcNow().AddDays(200))));

        report.Entries["Sim:certificate"].Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealth_WithinTheWarningWindow_IsDegraded()
    {
        var report = await CheckAsync(System("Sim", WritePfx("Sim", _clock.GetUtcNow().AddDays(10))));

        report.Entries["Sim:certificate"].Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task CheckHealth_ReportsNotAfterForTheStatusJob()
    {
        var notAfter = _clock.GetUtcNow().AddDays(200);

        var report = await CheckAsync(System("Sim", WritePfx("Sim", notAfter)));

        report.Entries["Sim:certificate"].Data.Should().ContainKey(ExternalSystemHealth.CertificateNotAfterKey);
    }

    [Fact]
    public async Task CheckHealth_WithAMissingFile_IsUnhealthy()
    {
        var report = await CheckAsync(System("Sim", Path.Combine(_directory, "absent.pfx")));

        report.Entries["Sim:certificate"].Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealth_WithTwoSystemsOneExpired_OnlyTheExpiredOneIsUnhealthy()
    {
        var config = System("Good", WritePfx("Good", _clock.GetUtcNow().AddDays(200)));
        foreach (var (key, value) in System("Expired", WritePfx("Expired", _clock.GetUtcNow().AddHours(-1))))
        {
            config[key] = value;
        }

        var report = await CheckAsync(config);

        report.Entries["Good:certificate"].Status.Should().Be(HealthStatus.Healthy);
        report.Entries["Expired:certificate"].Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task AddExternalSystems_ForASystemWithoutACertificate_RegistersNoCertificateCheck()
    {
        var report = await CheckAsync(System("Plain", pfx: null));

        report.Entries.Should().NotContainKey("Plain:certificate");
    }
}
