using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>
/// Removes job-run rows older than the configured retention period. Infrastructure owns what "old
/// enough" means and how the rows go, exactly as <c>IOutboxRetention</c> does.
/// </summary>
public interface IJobRunRetention
{
    /// <summary>Deletes runs past retention. Returns how many went.</summary>
    public Task<int> PruneAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Keeps <c>job_runs</c> bounded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Retention is part of this feature, not a follow-up.</b> A table that grows with job volume
/// forever would eventually be a memorable way to cause the incident the monitoring page exists to
/// detect. ADR 0021 says so; this is where it is enforced.
/// </para>
/// <para>
/// Parameterless, so it can be scheduled: a schedule fires with no caller and no arguments, which
/// <c>JobDescriptor.Scheduled&lt;TJob&gt;</c>'s <c>new()</c> constraint puts in the type system.
/// Light lane — it is one DELETE, not a scan. ADR 0017.
/// </para>
/// </remarks>
public sealed record PruneJobRuns : IJob
{
    public static JobLane Lane => JobLane.Light;
}

public sealed class PruneJobRunsHandler(IJobRunRetention retention)
{
    public Task Handle(PruneJobRuns job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // No logging of the count: Wolverine records the message lifecycle, and a routine sweep
        // that deleted N rows is the "it worked" noise root CLAUDE.md keeps out of the store.
        return retention.PruneAsync(cancellationToken);
    }
}
