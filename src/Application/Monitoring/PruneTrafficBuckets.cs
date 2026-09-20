using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>Removes traffic buckets older than the configured retention period.</summary>
public interface ITrafficRetention
{
    public Task<int> PruneAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Keeps <c>traffic_buckets</c> bounded.
/// </summary>
/// <remarks>
/// This table grows with REQUEST volume rather than with business events, which is a different
/// curve from everything else the monitoring page writes — and the reason ADR 0021 gives it seven
/// days rather than thirty. An unpruned traffic table would be a memorable way to cause the
/// incident this page exists to detect.
/// </remarks>
public sealed record PruneTrafficBuckets : IJob
{
    public static JobLane Lane => JobLane.Light;
}

public sealed class PruneTrafficBucketsHandler(ITrafficRetention retention)
{
    public Task Handle(PruneTrafficBuckets job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        return retention.PruneAsync(cancellationToken);
    }
}
