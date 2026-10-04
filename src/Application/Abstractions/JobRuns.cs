namespace AiFramework.Application.Abstractions;

/// <summary>
/// What became of one attempt at one job.
/// </summary>
/// <remarks>
/// There is deliberately no <c>DeadLettered</c> member. Dead-lettering is Wolverine's decision,
/// taken AFTER the handler returns and outside anything this application can observe from a
/// middleware — the recorder only ever sees an attempt succeed or fail. The terminal view lives
/// in Wolverine's own dead-letter table, which the monitoring page reads separately. Adding a
/// member here would mean a status nothing could ever write.
/// </remarks>
public enum JobRunStatus
{
    /// <summary>Started and not yet finished — or the worker died holding it.</summary>
    Running,

    Succeeded,

    /// <summary>Threw. Wolverine may still retry it; a later attempt is a separate row.</summary>
    Failed,
}

/// <summary>
/// One attempt beginning. <paramref name="Attempt"/> is Wolverine's own attempt counter, so a
/// retried job produces a second row rather than overwriting the first — which is what lets the
/// page show three failures as one job retried twice rather than as three separate jobs.
/// </summary>
/// <param name="EnvelopeId">Wolverine's message id. Stable across every attempt.</param>
/// <param name="Attempt">1 for the first try.</param>
/// <param name="JobName">The message type's name, e.g. <c>BuildOrderExport</c>.</param>
/// <param name="Lane">
/// Null only if the job is not in <c>JobRegistration.Jobs</c>, which the completeness test makes
/// impossible — recorded as nullable rather than guessed, so a gap shows up as a gap.
/// </param>
/// <param name="OwnerId">Null for a job that is not <see cref="IUserScopedJob"/>.</param>
/// <param name="TraceId">The W3C trace id, for the deep link into the log store.</param>
/// <param name="InstanceId">Which worker pod ran it.</param>
public sealed record JobRunAttempt(
    Guid EnvelopeId,
    int Attempt,
    string JobName,
    JobLane? Lane,
    Guid? OwnerId,
    string? TraceId,
    string? InstanceId);

/// <summary>
/// Records what jobs did, for the monitoring page. See ADR 0021.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every method writes immediately, outside any unit of work.</b> Two independent reasons, and
/// both matter: a run that FAILED is the one most worth recording, and its handler's transaction
/// is rolling back around it — so a tracked mutation would be discarded with it. And a middleware
/// that called <c>SaveChangesAsync</c> would commit the handler's half-finished work as a side
/// effect of recording that the handler had started. <c>OrderAuditWriter</c> sets the same
/// precedent for the same reason.
/// </para>
/// <para>
/// A failure here must never fail the job. The recorder is observability, not the work.
/// </para>
/// </remarks>
public interface IJobRunRecorder
{
    /// <summary>Records an attempt starting, as <see cref="JobRunStatus.Running"/>.</summary>
    public Task StartedAsync(JobRunAttempt attempt, CancellationToken cancellationToken);

    /// <summary>Marks the attempt succeeded and stamps its duration.</summary>
    public Task SucceededAsync(Guid envelopeId, int attempt, CancellationToken cancellationToken);

    /// <summary>Marks the attempt failed, with the exception's type and message.</summary>
    public Task FailedAsync(
        Guid envelopeId, int attempt, string error, CancellationToken cancellationToken);
}
