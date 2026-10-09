using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.ExternalSystems.Health;

/// <summary>
/// Runs in the WORKER only (ADR 0032): ASP.NET Core's health-check publisher hands it the
/// <c>external</c> checks' report every period, and it rewrites <c>external_system_status</c> and
/// the certificate gauge from it. Not a Quartz job, so the Jobs page is not buried in minutely runs.
/// </summary>
internal sealed class ExternalSystemStatusPublisher(
    IServiceScopeFactory scopes, ExternalSystemMetrics metrics, TimeProvider time) : IHealthCheckPublisher
{
    public async Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rows = Summarize(report, time.GetUtcNow());
        foreach (var row in rows)
        {
            metrics.SetCertificateNotAfter(row.Name, row.CertificateNotAfter);
        }

        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IExternalSystemStatusStore>()
            .ReplaceAsync(rows, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static IReadOnlyList<ExternalSystemStatusView> Summarize(HealthReport report, DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.Entries
            .Select(entry => (Name: entry.Key, Entry: entry.Value,
                System: entry.Value.Tags.FirstOrDefault(tag => !string.Equals(tag, ExternalSystemHealth.Tag, StringComparison.Ordinal))))
            .Where(check => check.System is not null)
            .GroupBy(check => check.System!, StringComparer.OrdinalIgnoreCase) // filtered to non-null above.
            .Select(system =>
            {
                // HealthStatus orders Unhealthy < Degraded < Healthy, so the minimum is the worst.
                var worst = system.MinBy(check => check.Entry.Status);
                var certificate = system.FirstOrDefault(
                    check => string.Equals(check.Name, ExternalSystemNames.CertificateCheck(system.Key), StringComparison.Ordinal));
                var token = system.FirstOrDefault(
                    check => string.Equals(check.Name, ExternalSystemNames.TokenCheck(system.Key), StringComparison.Ordinal));

                DateTimeOffset? notAfter = null;
                if (certificate.Name is not null
                    && certificate.Entry.Data.TryGetValue(ExternalSystemHealth.CertificateNotAfterKey, out var value)
                    && value is DateTimeOffset parsed)
                {
                    notAfter = parsed;
                }

                return new ExternalSystemStatusView(
                    system.Key,
                    ToState(worst.Entry.Status),
                    Describe(worst.Entry),
                    checkedAt,
                    notAfter,
                    token.Name is null ? null : token.Entry.Status == HealthStatus.Healthy);
            })
            .ToList();
    }

    /// <summary>A check that THREW is described by its type: its message can carry a host or a path.</summary>
    private static string? Describe(HealthReportEntry entry) =>
        entry.Exception is { } exception ? $"check failed: {exception.GetType().Name}" : entry.Description;

    private static ExternalSystemState ToState(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => ExternalSystemState.Healthy,
        HealthStatus.Degraded => ExternalSystemState.Degraded,
        _ => ExternalSystemState.Unhealthy,
    };
}
