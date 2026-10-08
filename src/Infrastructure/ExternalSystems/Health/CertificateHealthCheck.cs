using System.Globalization;
using AiFramework.Infrastructure.ExternalSystems.Certificates;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.ExternalSystems.Health;

internal sealed class CertificateHealthCheck(
    ICertificateProvider certificates,
    IOptionsMonitor<ExternalSystemsOptions> options,
    TimeProvider time,
    string systemName) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = certificates.GetCurrent(systemName);
        if (result.Certificate is null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(result.Problem ?? "no client certificate"));
        }

        var notAfter = result.Certificate.NotAfter;
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [ExternalSystemHealth.CertificateNotAfterKey] = notAfter,
        };
        var remaining = notAfter - time.GetUtcNow();
        var warning = options.CurrentValue.Find(systemName)?.CertificateExpiryWarning ?? TimeSpan.FromDays(30);
        var expires = notAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (remaining <= TimeSpan.Zero)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"client certificate expired on {expires}", data: data));
        }

        return Task.FromResult(remaining < warning
            ? HealthCheckResult.Degraded($"client certificate expires in {remaining.Days} days ({expires})", data: data)
            : HealthCheckResult.Healthy($"client certificate valid until {expires}", data: data));
    }
}
