using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>
/// Removes administrative audit rows older than the configured retention period.
/// </summary>
public interface IAdminActionRetention
{
    public Task<int> PruneAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Keeps <c>admin_actions</c> bounded, and with it the personal data this application holds:
/// the table records an address and a user-agent against two usernames.
/// </summary>
/// <remarks>
/// <b>Retention here is deliberately longer than <c>sign_in_events</c>', not the same.</b> The
/// two answer different questions. Sign-in attempts are high-volume operational noise where a
/// month is generous; a privilege change is rare, deliberate, and exactly the record wanted when
/// reconstructing how somebody came to have access long after the fact. The knob is separate
/// (<c>Monitoring__AdminActionRetentionDays</c>) precisely so the two can diverge. See ADR 0022.
/// </remarks>
public sealed record PruneAdminActions : IJob
{
    public static JobLane Lane => JobLane.Light;
}

public sealed class PruneAdminActionsHandler(IAdminActionRetention retention)
{
    public Task Handle(PruneAdminActions job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        return retention.PruneAsync(cancellationToken);
    }
}
