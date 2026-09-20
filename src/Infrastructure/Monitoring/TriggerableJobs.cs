using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Jobs;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// The scheduled jobs an operator may start on demand, resolved from
/// <see cref="JobRegistration.Jobs"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Name lookup against the registration list, never reflection over a caller's string.</b> The
/// list already holds an <c>EnqueueNew</c> delegate captured over the closed generic at
/// construction time, so starting a job stays reflection-free — the same posture
/// <c>AddCommand</c>/<c>AddQuery</c> hold, and the reason <c>JobDescriptor</c> carries the
/// delegate at all.
/// </para>
/// <para>
/// Enqueuing from the API is legitimate and needs no worker round trip: the API registers routing
/// for every lane and publishing is how a job starts (ADR 0016). It listens to nothing, so the
/// job runs on the worker regardless of who enqueued it.
/// </para>
/// </remarks>
internal sealed class TriggerableJobs(IJobScheduler scheduler) : ITriggerableJobs
{
    public IReadOnlyList<string> Names { get; } =
        [.. JobRegistration.Jobs.Where(job => job.IsScheduled).Select(job => job.Name)];

    public async Task<bool?> EnqueueAsync(string jobName, CancellationToken cancellationToken)
    {
        var descriptor = JobRegistration.Jobs
            .FirstOrDefault(job => string.Equals(job.Name, jobName, StringComparison.Ordinal));

        if (descriptor is null)
        {
            return null;
        }

        // Registered but not scheduled: it takes arguments a trigger cannot supply, which is the
        // same constraint JobDescriptor.Scheduled's new() puts in the type system. ADR 0017.
        if (descriptor.EnqueueNew is not { } enqueue)
        {
            return false;
        }

        await enqueue(scheduler, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
