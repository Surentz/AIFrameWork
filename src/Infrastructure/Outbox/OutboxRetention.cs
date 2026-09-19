using AiFramework.Application.Maintenance;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>
/// <see cref="IOutboxRetention"/> over <see cref="OutboxPoller.PruneAsync"/>, which is unchanged and
/// keeps its own Postgres test (<c>OutboxPollerTests.PruneAsync_DeletesProcessedRowsPastRetentionButKeepsDeadOnes</c>).
/// </summary>
public sealed class OutboxRetention(OutboxPoller poller) : IOutboxRetention
{
    public Task<int> PruneProcessedAsync(CancellationToken cancellationToken) =>
        poller.PruneAsync(cancellationToken);
}
