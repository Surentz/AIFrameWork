using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Maintenance;

/// <summary>
/// Removes outbox rows that were delivered longer ago than the retention period. Infrastructure
/// owns what "old enough" means (<c>OutboxOptions.RetentionPeriod</c>) and how the rows go.
/// </summary>
public interface IOutboxRetention
{
    /// <summary>Deletes processed rows past retention. Returns how many went.</summary>
    public Task<int> PruneProcessedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The first SCHEDULED job (ADR 0017), and the reference for writing one.
/// </summary>
/// <remarks>
/// <para>
/// Parameterless on purpose: a schedule fires with no caller and no arguments, so
/// <c>JobDescriptor.Scheduled&lt;TJob&gt;</c> requires <c>new()</c> and the compiler refuses to
/// schedule a job that needs data. A job that needs an owner is enqueued, never scheduled.
/// </para>
/// <para>
/// This used to run inside <c>OutboxPollerService</c>'s loop, in every pod that polls — both API
/// replicas and the worker — each issuing the same DELETE every five minutes. As a scheduled job it
/// runs once, on one worker, off the API.
/// </para>
/// </remarks>
public sealed record PruneProcessedOutbox : IJob
{
    public static JobLane Lane => JobLane.Light;
}

public sealed class PruneProcessedOutboxHandler(IOutboxRetention retention)
{
    public Task Handle(PruneProcessedOutbox job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // No logging of the count here: Wolverine records the message lifecycle, and a routine
        // sweep that deleted N rows is the "it worked" noise root CLAUDE.md keeps out of the store.
        return retention.PruneProcessedAsync(cancellationToken);
    }
}
