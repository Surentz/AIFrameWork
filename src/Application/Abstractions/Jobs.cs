namespace AiFramework.Application.Abstractions;

/// <summary>
/// Which queue a job runs on, and therefore what it may cost. The lane is the ONLY thing that
/// decides where a job runs — there is no per-job hosting choice to make.
/// </summary>
/// <remarks>
/// Both lanes are consumed by the worker; neither is consumed by the API. Separating them is
/// about head-of-line blocking, not about hosting: without two queues, one report would sit in
/// front of a queue of one-second emails. See ADR 0016.
/// </remarks>
public enum JobLane
{
    /// <summary>
    /// Milliseconds to a couple of seconds, a round trip or two, negligible CPU. Sending mail,
    /// nudging a webhook, writing an audit row.
    /// </summary>
    Light,

    /// <summary>
    /// Seconds to minutes, CPU- or memory-bound, or fanning over a large result set. Report
    /// generation, bulk import, recalculation. Runs at low parallelism deliberately: above the
    /// pod's core count, CPU-bound work only buys context switching. Scale replicas, not this.
    /// </summary>
    Heavy,
}

/// <summary>
/// Background work, dispatched to the worker. A job is a message and nothing more — there is no
/// base class, no runner, and no context object.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Lane"/> is <c>static abstract</c> so registration reads it with NO reflection and
/// no instance, the same posture <c>AddCommand</c>/<c>AddQuery</c> already hold — see
/// <c>src/Infrastructure/CLAUDE.md</c> on why dispatch must not be "simplified" into
/// <c>MakeGenericType</c>.
/// </para>
/// <para>
/// <b>A job carries the user it acts for.</b> There is no <c>HttpContext</c> in the worker, so
/// nothing can infer the caller: a job that reads or writes a user's data takes that user's id as
/// an ordinary property and the worker resolves <see cref="ICurrentUser"/> from it. ADR 0007 puts
/// ownership in the query itself, so a job that presents no user reads nothing rather than
/// reading everything.
/// </para>
/// <para>
/// <b>A job handler does not log its own outcome.</b> Anything it dispatches goes through
/// <c>Behaviors.LoggedAsync</c>, which already records outcome and duration; Wolverine logs the
/// message lifecycle. No "starting X", no "finished X".
/// </para>
/// </remarks>
public interface IJob
{
    /// <summary>The queue this job runs on. A literal on the type, never computed.</summary>
    public static abstract JobLane Lane { get; }
}

/// <summary>
/// A job that acts on one user's data, and therefore carries that user with it.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>HttpContext</c> in the worker, so nothing can infer the caller. A job that
/// implements this has <see cref="ICurrentUser"/> populated from <see cref="OwnerId"/> before its
/// handler runs — see <c>Infrastructure/Jobs/JobUserMiddleware.cs</c>.
/// </para>
/// <para>
/// <b>Implement this whenever the job reads or writes user-owned data.</b> ADR 0007 puts ownership
/// in the query itself, so the failure mode of forgetting is not a leak — <c>GetOrders</c> and
/// friends fail <c>Unauthorized</c> with no caller — but it is a job that never works, so the
/// choice is worth making deliberately rather than discovering.
/// </para>
/// </remarks>
public interface IUserScopedJob : IJob
{
    /// <summary>The user this job acts for.</summary>
    public Guid OwnerId { get; }
}

/// <summary>
/// Enqueues background work. The only way to start a job, and the seam that keeps Wolverine out
/// of this layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>An enqueue is NOT transactional with the caller's work.</b> This is the one thing to know
/// before using it. Calling <see cref="EnqueueAsync"/> from a command handler publishes
/// immediately: if the command's transaction then fails, the command is rolled back and the job
/// still runs, against state that was never committed.
/// </para>
/// <para>
/// <b>For a job that must not be lost, raise a domain event and enqueue from its handler.</b>
/// <c>DomainEventsInterceptor</c> writes the outbox row in the same <c>SaveChangesAsync</c> as
/// the aggregate, so the event — and therefore the job — exists if and only if the command
/// committed. That path is already at-least-once, and <c>DomainEventContext.MessageId</c> is the
/// dedupe key for making the handler idempotent.
/// </para>
/// <para>
/// This is not a design preference; it was measured. Wolverine's EF Core outbox
/// (<c>IDbContextOutbox&lt;T&gt;</c>) was the intended mechanism and does not fit: publishing
/// through it ENROLLS the DbContext, which opens a transaction, and the plain
/// <c>SaveChangesAsync</c> that <see cref="IUnitOfWork"/> issues then throws
/// ("NpgsqlRetryingExecutionStrategy does not support user-initiated transactions"). Adopting it
/// would mean wrapping every command's commit in an execution strategy AND swapping in
/// <c>SaveChangesAndFlushMessagesAsync</c> — rewriting the commit path to add a job framework.
/// See ADR 0016 and <c>JobEnqueueMechanismTests</c>, which pins the finding.
/// </para>
/// </remarks>
public interface IJobScheduler
{
    /// <summary>Runs the job as soon as a worker picks it up.</summary>
    public Task EnqueueAsync<TJob>(TJob job, CancellationToken cancellationToken)
        where TJob : IJob;

    /// <summary>Runs the job at a wall-clock time. Durable: it survives a worker restart.</summary>
    public Task ScheduleAsync<TJob>(TJob job, DateTimeOffset runAt, CancellationToken cancellationToken)
        where TJob : IJob;

    /// <summary>
    /// Runs the job after a delay. This is also how a recurring job recurs: the handler schedules
    /// its own next occurrence as its final act, so exactly one occurrence is in flight by
    /// construction and no distributed lock is needed across worker replicas. See ADR 0016.
    /// </summary>
    public Task ScheduleAsync<TJob>(TJob job, TimeSpan delay, CancellationToken cancellationToken)
        where TJob : IJob;
}
