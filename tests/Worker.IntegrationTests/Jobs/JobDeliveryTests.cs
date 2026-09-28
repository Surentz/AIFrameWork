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
    /// external transports by default, and a job goes OUT to a RabbitMQ queue and comes back IN
    /// through this host's listener — so without it the session sees the message "Sent" and stops
    /// waiting, and every assertion below fails with "No messages of type ... were received".
    /// That is exactly how this was found.
    /// <para>
    /// The timeout is generous headroom, not a target — it is a condition wait, not a sleep, so it
    /// returns the moment the handler finishes and only the failure path pays the full 30s.
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
    /// A scheduled job is held DURABLY, not run now - the recurring-job pattern depends on it.
    /// </summary>
    /// <remarks>
    /// RabbitMQ has no delayed delivery; Wolverine holds the envelope in its own Postgres storage
    /// until it is due and only then sends it to the lane's queue. Verified in the plan's Task 1
    /// (V6). Read directly rather than through a tracking session, which would only time out.
    /// </remarks>
    [Fact]
    public async Task AScheduledJob_IsHeldInWolverinesStorageUntilDue()
    {
        var job = new SendOrderConfirmation(Guid.NewGuid(), "SKU-JOB-LATER", 1);
        await ScheduleAsync(job, TimeSpan.FromHours(1));

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);

        await using var command = new NpgsqlCommand(
            "select count(*) from wolverine.wolverine_incoming_envelopes " +
            "where status = 'Scheduled' and message_type = 'scheduled-envelope' " +
            "and position('SendOrderConfirmation'::bytea in body) > 0",
            connection);

        var scheduled = Convert.ToInt64(
            await command.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture);

        scheduled.Should().BeGreaterThan(0,
            "a job scheduled an hour out must wait in Wolverine's storage - missing means it ran " +
            "immediately or was dropped, and a recurring job would spin or stop");
    }
}
