using AiFramework.Application.Maintenance;
using AiFramework.Infrastructure.Jobs;
using AiFramework.Infrastructure.Jobs.Scheduling;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Quartz;
using Wolverine.Tracking;

namespace AiFramework.Worker.IntegrationTests.Jobs;

/// <summary>
/// The scheduler, against the real worker host and the migrated schema. That the host starts at
/// all is the first assertion: Quartz runs in SchemaProvisioning.Validate, so migrations that do
/// not match the referenced Quartz package fail WorkerFactory's startup before any test body runs.
/// </summary>
[Collection(nameof(WorkerFactoryCollection))]
public sealed class SchedulingTests(WorkerFactory factory)
{
    private static JobDescriptor Prune => JobSchedules.Find(nameof(PruneProcessedOutbox))!;

    private async Task<IScheduler> SchedulerAsync() =>
        await factory.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(CancellationToken.None);

    private ScheduleSynchronizer Synchronizer => factory.Services.GetRequiredService<ScheduleSynchronizer>();

    [Fact]
    public async Task EveryScheduledJob_HasACronTriggerAfterStartup()
    {
        var scheduler = await SchedulerAsync();

        var trigger = await scheduler.GetTrigger(JobSchedules.TriggerKeyFor(Prune), CancellationToken.None);

        trigger.Should().BeAssignableTo<ICronTrigger>()
            .Which.CronExpressionString.Should().Be("0 5 * * * ?");
    }

    [Fact]
    public async Task APausedTrigger_StaysPausedAcrossAResync()
    {
        var scheduler = await SchedulerAsync();
        var key = JobSchedules.TriggerKeyFor(Prune);
        await scheduler.PauseTrigger(key, CancellationToken.None);

        try
        {
            await Synchronizer.SynchronizeAsync(CancellationToken.None);

            (await scheduler.GetTriggerState(key, CancellationToken.None))
                .Should().Be(TriggerState.Paused, "a pause from the monitoring page must survive redeploys");
        }
        finally
        {
            await scheduler.ResumeTrigger(key, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AJobNoLongerScheduled_IsRemovedOnResync()
    {
        var scheduler = await SchedulerAsync();
        var orphan = new JobKey("RetiredJob", JobSchedules.Group);
        await scheduler.AddJob(
            JobBuilder.Create<EnqueueScheduledJob>().WithIdentity(orphan).StoreDurably().Build(),
            AddJobOptions.Replacing,
            CancellationToken.None);

        await Synchronizer.SynchronizeAsync(CancellationToken.None);

        // Quartz 4.1 names this `Exists`, not `CheckExists` — there is no `CheckExists` on
        // IScheduler (confirmed against Quartz.xml: IScheduler.Exists(JobKey, ct) /
        // Exists(TriggerKey, ct) / Exists(string, ct) are the only three overloads).
        (await scheduler.Exists(orphan, CancellationToken.None)).Should().BeFalse(
            "removing .Scheduled(...) from the code must actually stop the job");
    }

    [Fact]
    public async Task FiringTheTrigger_EnqueuesTheJob_AndWolverineRunsIt()
    {
        var scheduler = await SchedulerAsync();
        var host = factory.Services.GetRequiredService<IHost>();

        // TriggerJob returns a ValueTask in Quartz 4.1, and ExecuteAndWaitAsync's lambda overload
        // needs a Task — .AsTask() is the bridge, not a workaround.
        var tracked = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(TimeSpan.FromSeconds(30))
            .WaitForExecutionOf<PruneProcessedOutbox>(1)
            .ExecuteAndWaitAsync(_ => scheduler.TriggerJob(
                JobSchedules.JobKeyFor(Prune), new JobDataMap(), CancellationToken.None).AsTask());

        tracked.Executed.SingleMessage<PruneProcessedOutbox>().Should().NotBeNull(
            "Quartz only decides when; Wolverine must be the one that runs the job");
    }

    /// <summary>
    /// Carried from Task 4's review: the worker's own AddQuartzHostedService must actually have
    /// started the scheduler by the time the host is up, not merely built it. A newly built
    /// scheduler sits in <see cref="SchedulerStatus.Created"/> and fires nothing.
    /// </summary>
    [Fact]
    public async Task TheScheduler_IsRunningAfterHostStartup()
    {
        var scheduler = await SchedulerAsync();

        scheduler.Status.Should().Be(
            SchedulerStatus.Running,
            "AddQuartzHostedService must have started the scheduler by the time the host answers " +
            "requests — Created or Standby would mean no trigger in this process ever fires");
    }

    /// <summary>
    /// Carried from Task 4's review: re-running the synchronizer with no code change must leave the
    /// stored trigger byte-for-byte the one already there, not a freshly built replacement.
    /// <see cref="ITrigger.StartTimeUtc"/> is what a fresh <c>TriggerBuilder.Create()</c> would set
    /// to "now" — if the synchronizer rebuilt the trigger despite an unchanged cron, this value
    /// would move on every resync.
    /// </summary>
    [Fact]
    public async Task ReSynchronizing_WithAnUnchangedCron_LeavesTheStoredTriggerUntouched()
    {
        var scheduler = await SchedulerAsync();
        var key = JobSchedules.TriggerKeyFor(Prune);

        var before = await scheduler.GetTrigger(key, CancellationToken.None);
        before.Should().NotBeNull();

        await Synchronizer.SynchronizeAsync(CancellationToken.None);

        var after = await scheduler.GetTrigger(key, CancellationToken.None);

        after.Should().NotBeNull();
        after.StartTimeUtc.Should().Be(
            before.StartTimeUtc,
            "an unchanged cron must leave the existing trigger completely untouched — a rebuilt " +
            "trigger would carry a new StartTimeUtc and silently discard any misfire catch-up the " +
            "untouched trigger existed to preserve");
    }

    /// <summary>
    /// Ruling 3: clustering only works if every node can tell itself apart. Quartz 4.1's
    /// unconfigured default is the literal string "NON_CLUSTERED" for every node — which would make
    /// every worker pod look like the same cluster node and defeat both single-fire and failover.
    /// <see cref="QuartzRegistration"/> registers <c>ProcessInstanceIdGenerator</c> specifically to
    /// avoid that; this is the end-to-end proof it actually took effect against the real store.
    /// </summary>
    [Fact]
    public async Task TheRunningScheduler_HasAUniqueNonSentinelInstanceId()
    {
        var scheduler = await SchedulerAsync();

        scheduler.SchedulerInstanceId.Should().NotBeNullOrWhiteSpace();
        scheduler.SchedulerInstanceId.Should().NotBe(
            "NON_CLUSTERED",
            "Quartz's own default for an unconfigured instance id generator — two pods sharing " +
            "that value would look like one cluster node to the store");
    }

    /// <summary>
    /// The other half of Ruling 3: two worker processes must not collide on the same id. Building a
    /// second host is cheap here because <c>WithWebHostBuilder</c> reuses the
    /// already-migrated Testcontainers Postgres instance rather than starting a new container —
    /// this is exactly the two-replica shape the clustering store must tolerate.
    /// </summary>
    [Fact]
    public async Task TwoWorkerHosts_GetDifferentSchedulerInstanceIds()
    {
        var first = await SchedulerAsync();

        await using var secondHost = factory.WithWebHostBuilder(_ => { });
        var second = await secondHost.Services.GetRequiredService<ISchedulerFactory>()
            .GetScheduler(CancellationToken.None);

        second.SchedulerInstanceId.Should().NotBe(
            first.SchedulerInstanceId,
            "two worker processes sharing an instance id would look like one cluster node, " +
            "defeating both single-fire and failover");
    }

    /// <summary>
    /// Ruling 9: confirms Quartz's own health check actually surfaces through the worker's
    /// <c>/health/ready</c> pipeline, rather than merely being registered on the IQuartzBuilder and
    /// never joining the ASP.NET Core health check registry the endpoint reads from.
    /// </summary>
    [Fact]
    public async Task TheQuartzHealthCheck_IsRegisteredAndHealthy()
    {
        var healthChecks = factory.Services.GetRequiredService<HealthCheckService>();

        var report = await healthChecks.CheckHealthAsync(CancellationToken.None);

        report.Entries.Keys.Should().Contain(
            key => key.Contains("quartz", StringComparison.OrdinalIgnoreCase),
            "AddQuartzHealthChecks() must register a check the worker's own HealthCheckService " +
            "reports, or /health/ready never actually reflects the scheduler's state");
    }

    /// <summary>
    /// Quartz 4.2 reads and writes three continuation columns on every trigger it stores, and a
    /// 4.2 node refuses to start without them. Validate at startup already proves they exist; this
    /// pins what that cannot: that they are in the `quartz` schema this repo uses, not the
    /// unqualified table Quartz's own upgrade script names, and that their types are Quartz's.
    /// </summary>
    [Fact]
    public async Task TheTriggersTable_HasQuartz42sContinuationColumns()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);

        await using var command = new NpgsqlCommand(
            "select column_name, data_type, is_nullable from information_schema.columns " +
            "where table_schema = 'quartz' and table_name = 'qrtz_triggers' " +
            "and column_name in ('continues_trigger_name', 'continues_trigger_group', 'continuation_condition') " +
            "order by column_name",
            connection);

        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                columns.Add($"{reader.GetString(0)} {reader.GetString(1)} {reader.GetString(2)}");
            }
        }

        columns.Should().Equal(
            "continuation_condition integer YES",
            "continues_trigger_group text YES",
            "continues_trigger_name text YES");
    }

    /// <summary>
    /// Quartz 4.3 adds twelve columns across four tables (fire progress, overlap policy, pause
    /// reason), and a 4.3 node refuses to start without them. As for 4.2's, startup validation
    /// proves they exist; this pins that they are in the `quartz` schema, nullable, and of
    /// Quartz's own types.
    /// </summary>
    [Fact]
    public async Task TheQuartzTables_HaveQuartz43sColumns()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);

        await using var command = new NpgsqlCommand(
            "select table_name, column_name, data_type, character_maximum_length, is_nullable " +
            "from information_schema.columns where table_schema = 'quartz' " +
            "and column_name in ('progress', 'progress_message', 'overlap_policy', " +
            "'pause_reason', 'paused_by', 'paused_at') " +
            // C collation, so 'pause_reason' sorts before 'paused_at' whatever the server's locale.
            "order by table_name collate \"C\", column_name collate \"C\"",
            connection);

        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                var length = reader.IsDBNull(3) ? "" : $"({reader.GetInt32(3)})";
                columns.Add(
                    $"{reader.GetString(0)}.{reader.GetString(1)} " +
                    $"{reader.GetString(2)}{length} {reader.GetString(4)}");
            }
        }

        columns.Should().Equal(
            "qrtz_fired_triggers.progress integer YES",
            "qrtz_fired_triggers.progress_message character varying(250) YES",
            "qrtz_paused_job_grps.pause_reason character varying(250) YES",
            "qrtz_paused_job_grps.paused_at bigint YES",
            "qrtz_paused_job_grps.paused_by character varying(200) YES",
            "qrtz_paused_trigger_grps.pause_reason character varying(250) YES",
            "qrtz_paused_trigger_grps.paused_at bigint YES",
            "qrtz_paused_trigger_grps.paused_by character varying(200) YES",
            "qrtz_triggers.overlap_policy integer YES",
            "qrtz_triggers.pause_reason character varying(250) YES",
            "qrtz_triggers.paused_at bigint YES",
            "qrtz_triggers.paused_by character varying(200) YES");
    }
}
