using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>Every external system's last health and its last hour of traffic. Not cached (ADR 0021).</summary>
public sealed record GetExternalSystemStatus : IQuery<ExternalSystemsView>;

public sealed record ExternalSystemsView(DateTimeOffset TrafficSince, IReadOnlyList<ExternalSystemRowView> Systems);

/// <summary>
/// One system. <see cref="State"/> is null when the worker has never checked it (only traffic is
/// known). <see cref="Stale"/> means the worker stopped writing: the last status is history, not health.
/// </summary>
public sealed record ExternalSystemRowView(
    string Name,
    ExternalSystemState? State,
    string? Description,
    DateTimeOffset? CheckedAt,
    bool Stale,
    DateTimeOffset? CertificateNotAfter,
    bool? TokenOk,
    long Calls,
    long Failed,
    long Faulted,
    long Attempts,
    double? P95Ms);

public sealed class GetExternalSystemStatusHandler(
    IExternalSystemStatusReader statuses, ITrafficReader traffic, IClock clock)
    : IQueryHandler<GetExternalSystemStatus, ExternalSystemsView>
{
    /// <summary>Three missed one-minute checks: the worker is not running, or not reaching the database.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan TrafficWindow = TimeSpan.FromHours(1);

    public async Task<Result<ExternalSystemsView>> HandleAsync(
        GetExternalSystemStatus query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var now = clock.UtcNow;
        var since = now - TrafficWindow;
        var rows = await statuses.ListAsync(cancellationToken).ConfigureAwait(false);
        var calls = await traffic.OutboundAsync(since, cancellationToken).ConfigureAwait(false);

        var statusByName = rows.ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);
        var trafficByName = calls.ToDictionary(row => row.System, StringComparer.OrdinalIgnoreCase);

        var systems = statusByName.Keys
            .Union(trafficByName.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                var status = statusByName.GetValueOrDefault(name);
                var used = trafficByName.GetValueOrDefault(name);
                return new ExternalSystemRowView(
                    status?.Name ?? used!.System, // one of the two exists: name came from their union.
                    status?.State,
                    status?.Description,
                    status?.CheckedAt,
                    status is not null && now - status.CheckedAt > StaleAfter,
                    status?.CertificateNotAfter,
                    status?.TokenOk,
                    used?.Calls ?? 0,
                    used?.Failed ?? 0,
                    used?.Faulted ?? 0,
                    used?.Attempts ?? 0,
                    used?.P95Ms);
            })
            .ToList();

        return Result.Success(new ExternalSystemsView(since, systems));
    }
}
