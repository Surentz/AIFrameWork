using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// Deletes traffic buckets past retention with one set-based statement, like the other two
/// sweeps and for the same reasons.
/// </summary>
internal sealed class TrafficRetention(
    AiFrameworkDbContext context, IOptions<MonitoringOptions> options, IClock clock)
    : ITrafficRetention
{
    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-options.Value.TrafficRetentionDays);

        return context.TrafficBuckets
            .Where(bucket => bucket.BucketStart < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
