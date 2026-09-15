using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Infrastructure.Jobs;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace AiFramework.Worker.IntegrationTests.Jobs;

/// <summary>
/// The other half of <c>ApiPublishesOnlyTests</c>: the API listens on nothing, and this host
/// listens on everything. Together they are ADR 0016's split, asserted rather than intended.
/// </summary>
[Collection(nameof(WorkerFactoryCollection))]
public sealed class JobDeliveryTests(WorkerFactory factory)
{
    [Fact]
    public async Task TheWorker_ListensOnEveryLane()
    {
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();

        var capabilities = await ServiceCapabilities.ReadFrom(
            runtime, new Uri("local://worker-lane-test"), CancellationToken.None);

        var listening = capabilities.MessagingEndpoints
            .Where(endpoint => endpoint.IsListener)
            .Select(endpoint => endpoint.Uri.ToString())
            .ToList();

        foreach (var lane in Enum.GetValues<JobLane>())
        {
            listening.Should().Contain(
                uri => uri.Contains(JobRegistration.QueueFor(lane), StringComparison.OrdinalIgnoreCase),
                $"the worker consumes every lane, and {lane} had no listener — a lane nobody " +
                "listens on is a queue that fills silently");
        }
    }

    /// <summary>
    /// <b>IncludeExternalTransports is required, not a tuning knob.</b> A tracking session ignores
    /// external transports by default, and a job goes OUT to a Postgres queue and comes back IN
    /// through this host's listener — so without it the session sees the message "Sent" and stops
    /// waiting, and every assertion below fails with "No messages of type ... were received".
    /// That is exactly how this was found.
    /// <para>
    /// The timeout is generous because the Postgres transport polls; it is a condition wait, not a
    /// sleep, so it returns the moment the handler finishes and only the failure path pays it.
    /// </para>
    /// </summary>
    private static TrackedSessionConfiguration TrackJobs(IHost host) =>
        host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(TimeSpan.FromSeconds(30));

    /// <summary>
    /// Enqueues from the worker's own container, in its own scope.
    /// </summary>
    /// <remarks>
    /// Declared to return <see cref="Task"/> rather than passed as an inline <c>async</c> lambda
    /// because <c>ExecuteAndWaitAsync</c> overloads on <c>Func&lt;IMessageContext, Task&gt;</c> and
    /// <c>Func&lt;IMessageContext, ValueTask&gt;</c>, and a void-returning async lambda is
    /// ambiguous between them (CS0121).
    /// </remarks>
    private async Task EnqueueAsync<TJob>(TJob job)
        where TJob : IJob
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        await jobs.EnqueueAsync(job, CancellationToken.None);
    }

    /// <summary>The delayed equivalent of <see cref="EnqueueAsync"/>, same overload reasoning.</summary>
    private async Task ScheduleAsync<TJob>(TJob job, TimeSpan delay)
        where TJob : IJob
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobScheduler>();

        await jobs.ScheduleAsync(job, delay, CancellationToken.None);
    }

    [Fact]
    public async Task ALightJob_ReachesItsHandler()
    {
        var host = factory.Services.GetRequiredService<IHost>();

        // TrackActivity waits for the message to be fully handled, so this is deterministic
        // rather than racing the listener. tests/CLAUDE.md's no-sleep rule requires it.
        var tracked = await TrackJobs(host).ExecuteAndWaitAsync(_ => EnqueueAsync(new SendOrderConfirmation(Guid.NewGuid(), "SKU-JOB-LIGHT", 2)));

        tracked.Executed.SingleMessage<SendOrderConfirmation>()
            .Should().NotBeNull("an enqueued light job must reach its handler");
    }

    [Fact]
    public async Task AUserScopedJob_ResolvesItsOwnerAsTheCurrentUser()
    {
        var ownerId = Guid.NewGuid();
        var host = factory.Services.GetRequiredService<IHost>();

        // RebuildOrderReportHandler pages GetOrders, which fails Unauthorized with no caller
        // (ADR 0007 puts ownership in the query) and THROWS from the handler when it does. So a
        // job that completes without throwing is itself the proof that JobUserMiddleware
        // populated ICurrentUser from OwnerId — there is no other way for it to have succeeded.
        var tracked = await TrackJobs(host).ExecuteAndWaitAsync(_ => EnqueueAsync(new RebuildOrderReport(ownerId)));

        tracked.Executed.SingleMessage<RebuildOrderReport>()
            .Should().NotBeNull("the job must reach its handler");

        tracked.AllExceptions().Should().BeEmpty(
            "the handler pages GetOrders, which fails Unauthorized without a current user — so " +
            "any exception here means JobUserMiddleware did not set the caller from OwnerId");
    }

    /// <summary>
    /// A scheduled job is held DURABLY, not run now. This is what makes the recurring-job pattern
    /// work at all: a handler schedules its own next occurrence, so if ScheduleAsync ran the job
    /// immediately, every recurring job would become a tight loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where a scheduled job actually lives was discovered, not assumed.</b> It is NOT in
    /// <c>wolverine.wolverine_incoming_envelopes</c> — every table in the <c>wolverine</c> schema
    /// is empty at this point. The Postgres QUEUE transport provisions a schema of its own,
    /// <c>wolverine_queues</c>, holding one table per queue plus a <c>_scheduled</c> companion:
    /// <c>wolverine_queue_jobs_light</c> and <c>wolverine_queue_jobs_light_scheduled</c>. That
    /// second table is where a delayed job waits. Found by dumping every table in every schema
    /// after a schedule; the first version of this test asserted against the envelope table and
    /// failed against a correct implementation.
    /// </para>
    /// <para>
    /// Deliberately NOT written with a tracking session: <c>ExecuteAndWaitAsync</c> waits for the
    /// message to be handled, and this message must never be handled, so the session would simply
    /// time out — an absence of evidence rather than evidence of absence, bought at the price of
    /// the full timeout. Reading the row back asserts the stronger thing directly, and never waits.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AScheduledJob_IsHeldInTheLanesScheduledTable()
    {
        await ScheduleAsync(
            new SendOrderConfirmation(Guid.NewGuid(), "SKU-JOB-LATER", 1),
            TimeSpan.FromHours(1));

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);

        // SendOrderConfirmation is a Light job, so it must be waiting on the LIGHT lane's
        // scheduled table — which also proves lane routing survives scheduling.
        var lightQueue = JobRegistration.QueueFor(JobLane.Light);

        await using var command = new NpgsqlCommand(
            $"select count(*) from wolverine_queues.wolverine_queue_{lightQueue}_scheduled",
            connection);

        var scheduled = (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;

        scheduled.Should().BeGreaterThan(
            0,
            "a job scheduled an hour out must be persisted in its lane's scheduled table — if it " +
            "is missing, ScheduleAsync either ran it immediately or dropped it, and a recurring " +
            "job would either spin or stop");
    }
}
