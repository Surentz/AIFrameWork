using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>
/// Removes sign-in events older than the configured retention period.
/// </summary>
public interface ISignInEventRetention
{
    public Task<int> PruneAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Keeps <c>sign_in_events</c> bounded — and, because that table holds IP addresses and
/// user-agents against usernames, keeps this application's personal data bounded too.
/// </summary>
/// <remarks>
/// <b>This is a requirement, not housekeeping.</b> ADR 0021 records the retention decision as
/// part of the feature precisely because a permanent log of who signed in from where is a
/// different thing from a thirty-day one, and only one of them was agreed to.
/// </remarks>
public sealed record PruneSignInEvents : IJob
{
    public static JobLane Lane => JobLane.Light;
}

public sealed class PruneSignInEventsHandler(ISignInEventRetention retention)
{
    public Task Handle(PruneSignInEvents job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        return retention.PruneAsync(cancellationToken);
    }
}
