using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>
/// Runs a scheduled job now, without waiting for its cron.
/// </summary>
/// <remarks>
/// <para>
/// Only a job that is <b>scheduled</b> can be triggered, and that is not a policy decision but the
/// same constraint ADR 0017 already enforces in the type system: a schedule fires with no caller
/// and no arguments, so <c>JobDescriptor.Scheduled&lt;TJob&gt;</c> requires <c>new()</c>. A job
/// that needs an owner or an id has nothing this endpoint could supply.
/// </para>
/// <para>
/// The lookup is by NAME against the registration list, never by reflecting over the assembly for
/// a type matching a string the caller sent. A caller naming an unregistered job gets a
/// <c>NotFound</c>; a caller naming a registered but unscheduled one gets a
/// <c>Validation</c> failure that says so.
/// </para>
/// </remarks>
public sealed record TriggerJob(string JobName) : ICommand<bool>;

/// <summary>
/// The jobs this endpoint may start, resolved from the registration list. Implemented in
/// Infrastructure, where <c>JobRegistration</c> lives; a port so <c>Application</c> stays free of
/// Wolverine and of the registry's concrete shape.
/// </summary>
public interface ITriggerableJobs
{
    /// <summary>Every scheduled job's name, for the page to offer.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>
    /// Enqueues the named job. Null when no job of that name is registered; false when it is
    /// registered but not scheduled, and therefore not triggerable.
    /// </summary>
    public Task<bool?> EnqueueAsync(string jobName, CancellationToken cancellationToken);
}

public sealed class TriggerJobHandler(ITriggerableJobs jobs) : ICommandHandler<TriggerJob, bool>
{
    public async Task<Result<bool>> HandleAsync(
        TriggerJob command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var enqueued = await jobs.EnqueueAsync(command.JobName, cancellationToken)
            .ConfigureAwait(false);

        return enqueued switch
        {
            true => Result.Success(true),
            false => Result.Failure<bool>(new Error(
                ErrorKind.Validation,
                "job.not_scheduled",
                $"'{command.JobName}' is not a scheduled job, so it takes arguments this cannot supply.")),
            null => Result.Failure<bool>(new Error(
                ErrorKind.NotFound, "job.not_found", $"No job named '{command.JobName}'.")),
        };
    }
}
