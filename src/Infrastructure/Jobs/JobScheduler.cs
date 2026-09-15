using AiFramework.Application.Abstractions;
using Wolverine;

namespace AiFramework.Infrastructure.Jobs;

/// <summary>
/// <see cref="IJobScheduler"/> over Wolverine's message bus. The only place Application's job
/// abstraction meets a Wolverine type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Publishes through the plain bus, NOT through IDbContextOutbox&lt;AiFrameworkDbContext&gt;.</b>
/// That was the intended mechanism and it was measured not to fit — the finding, from
/// JobEnqueueMechanismTests (2026-09-15):
/// </para>
/// <list type="number">
/// <item>Resolving the EF Core outbox and publishing ENROLLS the DbContext, which opens a
/// transaction. The plain <c>SaveChangesAsync</c> that <c>UnitOfWork</c> issues then throws:
/// "The configured execution strategy 'NpgsqlRetryingExecutionStrategy' does not support
/// user-initiated transactions."</item>
/// <item>Wrapped in <c>Database.CreateExecutionStrategy().ExecuteAsync(...)</c> so it gets past
/// that, a plain <c>SaveChangesAsync</c> persists NO outgoing envelope —
/// <c>wolverine_outgoing_envelopes</c> is unchanged and the message is lost with the scope.</item>
/// <item>Only <c>SaveChangesAndFlushMessagesAsync</c> persists and delivers.</item>
/// </list>
/// <para>
/// Adopting it would therefore mean changing every command in the system: wrapping the
/// unit-of-work commit in an execution strategy AND swapping in
/// <c>SaveChangesAndFlushMessagesAsync</c>. Rewriting the commit path to add a job framework was
/// refused. See ADR 0016.
/// </para>
/// <para>
/// The consequence callers must know, stated on <see cref="IJobScheduler"/> itself: an enqueue is
/// not transactional with the caller's work. A job that must not be lost is enqueued from an
/// <c>IDomainEventHandler</c>, whose own delivery already is —
/// <c>OrderPlacedConfirmationHandler</c> is the reference.
/// </para>
/// </remarks>
public sealed class JobScheduler(IMessageBus bus) : IJobScheduler
{
    public Task EnqueueAsync<TJob>(TJob job, CancellationToken cancellationToken)
        where TJob : IJob
    {
        ArgumentNullException.ThrowIfNull(job);

        // Routing to the lane's queue is a registration concern (JobRegistration.MapJobs), not a
        // per-call one: PublishAsync consults the routing rules for TJob. Naming the queue here
        // would be a second place for lane-to-queue mapping to live, and the two would drift.
        return bus.PublishAsync(job).AsTask();
    }

    public Task ScheduleAsync<TJob>(TJob job, DateTimeOffset runAt, CancellationToken cancellationToken)
        where TJob : IJob
    {
        ArgumentNullException.ThrowIfNull(job);

        return bus.ScheduleAsync(job, runAt).AsTask();
    }

    public Task ScheduleAsync<TJob>(TJob job, TimeSpan delay, CancellationToken cancellationToken)
        where TJob : IJob
    {
        ArgumentNullException.ThrowIfNull(job);

        return bus.ScheduleAsync(job, delay).AsTask();
    }
}
