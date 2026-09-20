using System.Diagnostics;
using System.Runtime.ExceptionServices;
using AiFramework.Application.Abstractions;
using Wolverine;

namespace AiFramework.Infrastructure.Jobs;

/// <summary>
/// Records every job attempt and its outcome, for the monitoring page. Applied to every job chain
/// by <c>JobRegistration</c>, so a new job is recorded without doing anything. See ADR 0021.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape of this class is dictated by Wolverine's codegen, not by taste</b>, and the
/// measurements behind each constraint are in <c>src/Worker/CLAUDE.md</c>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="AfterAsync"/> is generated INSIDE the try, after the handler call, so it runs on
/// success only. That is the whole mechanism for telling success from failure — <c>Envelope</c>
/// carries no outcome, and a <c>Finally</c> method runs before <c>OnException</c> and so cannot
/// know one either.
/// </description></item>
/// <item><description>
/// <see cref="OnExceptionAsync"/> takes the exception FIRST. With <c>Envelope</c> in front of it
/// the method is silently dropped from the generated adapter — no warning, green build.
/// </description></item>
/// <item><description>
/// <b>The generated catch block emits no rethrow.</b> Without the
/// <see cref="ExceptionDispatchInfo"/> call at the end of <see cref="OnExceptionAsync"/> this
/// middleware would swallow every job failure and silently disable the retry and dead-letter
/// policy in <c>JobRegistration</c> — jobs would look successful, never retry, and never reach
/// the error queue. <c>JobRunRecordingTests</c> pins that behaviour precisely because nothing
/// else would catch its loss.
/// </description></item>
/// <item><description>
/// The methods take <c>Envelope</c> rather than <c>IJob</c>: JasperFx matches chain variables by
/// exact type and will not upcast a concrete message to an interface. <c>JobUserMiddleware</c>
/// carries the same workaround.
/// </description></item>
/// </list>
/// <para>
/// <b>A recorder failure must never fail the job.</b> Recording is observability; the work is the
/// work. Nothing here catches, though — the recorder's own writes are single statements against
/// the same connection the handler just used, and swallowing a database fault here would hide a
/// broken monitoring table behind green jobs. If that trade is ever revisited, revisit it with a
/// test.
/// </para>
/// </remarks>
public static class JobRunMiddleware
{
    public static Task BeforeAsync(
        Envelope envelope, IJobRunRecorder recorder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(recorder);

        var message = envelope.Message;
        var jobType = message?.GetType();

        // The lane comes from the registration list, not from the message: IJob.Lane is a static
        // abstract, so reading it from an instance is impossible, and JobRegistration already
        // holds the mapping that the completeness test guarantees is total.
        var descriptor = jobType is null ? null : JobRegistration.DescriptorFor(jobType);

        return recorder.StartedAsync(
            new JobRunAttempt(
                EnvelopeId: envelope.Id,
                Attempt: Math.Max(1, envelope.Attempts),
                JobName: descriptor?.Name ?? jobType?.Name ?? "unknown",
                Lane: descriptor?.Lane,
                OwnerId: message is IUserScopedJob job ? job.OwnerId : null,
                TraceId: Activity.Current?.TraceId.ToString(),
                InstanceId: Environment.GetEnvironmentVariable("HOSTNAME")),
            cancellationToken);
    }

    public static Task AfterAsync(
        Envelope envelope, IJobRunRecorder recorder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(recorder);

        return recorder.SucceededAsync(
            envelope.Id, Math.Max(1, envelope.Attempts), cancellationToken);
    }

    /// <summary>
    /// Records the failure and RETHROWS. The rethrow is load-bearing — see the class remarks.
    /// </summary>
    public static async Task OnExceptionAsync(
        Exception exception,
        Envelope envelope,
        IJobRunRecorder recorder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(recorder);

        // Type and message, never the whole ToString(): a full stack trace belongs in the log
        // store, which the row's TraceId links to, not in a column an operator scans in a table.
        var error = $"{exception.GetType().Name}: {exception.Message}";

        await recorder
            .FailedAsync(envelope.Id, Math.Max(1, envelope.Attempts), error, cancellationToken)
            .ConfigureAwait(false);

        // NOT `throw exception;` — CA2200 is an error here and it would erase the original stack.
        // Capture().Throw() preserves it and leaves Wolverine's retry and dead-letter handling
        // exactly as it would be with no middleware at all.
        ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
