using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// Deletes administrative audit rows past retention, with one set-based statement like the other
/// sweeps. This table is personal data, so the sweep is a requirement rather than tidiness.
/// </summary>
internal sealed class AdminActionRetention(
    AiFrameworkDbContext context, IOptions<MonitoringOptions> options, IClock clock)
    : IAdminActionRetention
{
    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-options.Value.AdminActionRetentionDays);

        return context.AdminActions
            .Where(a => a.At < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
