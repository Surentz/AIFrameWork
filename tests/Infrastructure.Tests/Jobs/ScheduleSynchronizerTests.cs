using AiFramework.Application.Maintenance;
using AiFramework.Infrastructure.Jobs;
using AiFramework.Infrastructure.Jobs.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace AiFramework.Infrastructure.Tests.Jobs;

/// <summary>
/// Exercises <see cref="ScheduleSynchronizer"/> against a real Quartz scheduler — an in-memory
/// store built through the same DI surface <c>QuartzRegistration</c> uses, per the repo's "never
/// mock a type you do not own" rule (<c>tests/CLAUDE.md</c>): <c>IScheduler</c>/<c>ICronTrigger</c>
/// are Quartz's own types, not ours to substitute. <c>Start()</c> is never called — job-store reads
/// and writes (<c>ScheduleJob</c>/<c>GetTrigger</c>/<c>RescheduleJob</c>/<c>PauseTrigger</c>) work
/// without a running scheduling loop, so this stays a fast, deterministic unit test with no host
/// and no database, matching <see cref="JobSchedules"/>'s own testability goal.
/// </summary>
/// <remarks>
/// A genuine misfire (a trigger whose <c>NextFireTimeUtc</c> is actually in the past) could not be
/// reproduced here: Quartz's own <c>ComputeFirstFireTimeUtc</c> always advances a freshly stored
/// trigger to the next occurrence at-or-after the real system clock, even with a past
/// <c>StartAt</c>, and even with a <see cref="Quartz.IQuartzBuilder.UseTimeProvider(TimeProvider)"/>
/// fake clock registered on the scheduler — measured directly, not assumed: both attempts landed on
/// the real wall-clock's next occurrence, not the fake one. Producing a true misfire needs either a
/// running scheduler crossing a real fire time (a wall-clock wait this repo's own testing rules
/// forbid) or reaching into store internals no public API exposes. The tests below instead prove
/// the same guarantee ("untouched" means untouched) a clock-independent way: a sentinel written
/// into the stored trigger's own <c>JobDataMap</c>, which only <c>ScheduleSynchronizer</c>'s own
/// <c>TriggerBuilder</c> call — never invoked when the cron is unchanged — would ever discard.
/// Task 6 covers the actual misfire-survives-a-restart guarantee end to end, against a running
/// worker host.
/// </remarks>
public sealed class ScheduleSynchronizerTests
{
    private const string SentinelKey = "test-sentinel";

    private static readonly JobDescriptor PruneJob = JobSchedules.Find(nameof(PruneProcessedOutbox))!;

    [Fact]
    public async Task SynchronizeAsync_WithAnUnchangedCron_LeavesTheStoredTriggerObjectUntouched()
    {
        // Task 4 fix round 1: the bug this pins. Before the fix, UpsertAsync rebuilt and
        // RescheduleJob'd every trigger unconditionally, even when the cron had not changed —
        // discarding whatever a live scheduler had already recorded on it (in particular, an
        // overdue NextFireTimeUtc a misfire-catch-up depends on). A sentinel in the trigger's own
        // JobDataMap — which ScheduleSynchronizer's TriggerBuilder never sets — proves whether the
        // stored trigger is the ORIGINAL one or a freshly built replacement, with no dependency on
        // clock behaviour at all.
        var (scheduler, synchronizer, provider) = await BuildAsync(new JobOptions());
        await using var _ = provider;

        var jobKey = JobSchedules.JobKeyFor(PruneJob);
        var triggerKey = JobSchedules.TriggerKeyFor(PruneJob);
        var cron = PruneJob.DefaultCron!;

        var original = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .WithCronSchedule(cron, b => b.WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed))
            .UsingJobData(SentinelKey, "original")
            .Build();

        await scheduler.ScheduleJob(
            JobBuilder.Create<EnqueueScheduledJob>().WithIdentity(jobKey).StoreDurably().Build(),
            original,
            default,
            CancellationToken.None);

        await synchronizer.SynchronizeAsync(CancellationToken.None);

        var after = await scheduler.GetTrigger(triggerKey, CancellationToken.None);
        after.Should().NotBeNull();
        after.JobDataMap.GetString(SentinelKey).Should().Be(
            "original",
            "an unchanged cron must leave the existing trigger completely untouched, not replace it " +
            "with a freshly built one");
    }

    [Fact]
    public async Task SynchronizeAsync_WithAnUnchangedCronOnAPausedTrigger_LeavesItPaused()
    {
        var (scheduler, synchronizer, provider) = await BuildAsync(new JobOptions());
        await using var _ = provider;

        var jobKey = JobSchedules.JobKeyFor(PruneJob);
        var triggerKey = JobSchedules.TriggerKeyFor(PruneJob);
        var cron = PruneJob.DefaultCron!;

        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .WithCronSchedule(cron, b => b.WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed))
            .Build();

        await scheduler.ScheduleJob(
            JobBuilder.Create<EnqueueScheduledJob>().WithIdentity(jobKey).StoreDurably().Build(),
            trigger,
            default,
            CancellationToken.None);
        await scheduler.PauseTrigger(triggerKey, CancellationToken.None);

        await synchronizer.SynchronizeAsync(CancellationToken.None);

        (await scheduler.GetTriggerState(triggerKey, CancellationToken.None)).Should().Be(TriggerState.Paused);
    }

    [Fact]
    public async Task SynchronizeAsync_WithAChangedCron_ReschedulesTheTrigger()
    {
        // Control test: a real cron change must still take effect — the fix above must narrow the
        // bug's window, not remove rescheduling altogether. A changed cron must also lose the
        // sentinel: it is the one case where replacing the trigger is correct.
        const string changedCron = "0 10 * * * ?";
        var (scheduler, synchronizer, provider) = await BuildAsync(
            new JobOptions { Schedules = { [PruneJob.Name] = changedCron } });
        await using var _ = provider;

        var jobKey = JobSchedules.JobKeyFor(PruneJob);
        var triggerKey = JobSchedules.TriggerKeyFor(PruneJob);

        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .WithCronSchedule(PruneJob.DefaultCron!, b => b.WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed))
            .UsingJobData(SentinelKey, "original")
            .Build();

        await scheduler.ScheduleJob(
            JobBuilder.Create<EnqueueScheduledJob>().WithIdentity(jobKey).StoreDurably().Build(),
            trigger,
            default,
            CancellationToken.None);

        await synchronizer.SynchronizeAsync(CancellationToken.None);

        var after = await scheduler.GetTrigger(triggerKey, CancellationToken.None) as ICronTrigger;
        after.Should().NotBeNull();
        after.CronExpressionString.Should().Be(changedCron);
        after.JobDataMap.ContainsKey(SentinelKey).Should().BeFalse(
            "a real cron change must rebuild the trigger, which is exactly why it cannot preserve the sentinel");
    }

    private static async Task<(IScheduler Scheduler, ScheduleSynchronizer Synchronizer, ServiceProvider Provider)> BuildAsync(
        JobOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQuartz(q => q.UseInMemoryStore(_ => { }));

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISchedulerFactory>();

        // Never Start()'d: job-store CRUD (ScheduleJob/GetTrigger/RescheduleJob/PauseTrigger) needs
        // no running scheduling loop, and not starting one keeps this test from ever actually
        // firing EnqueueScheduledJob.
        var scheduler = await factory.GetScheduler(CancellationToken.None);

        var synchronizer = new ScheduleSynchronizer(
            factory,
            Options.Create(options),
            provider.GetRequiredService<ILogger<ScheduleSynchronizer>>());

        return (scheduler, synchronizer, provider);
    }
}
