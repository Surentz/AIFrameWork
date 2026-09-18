using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace AiFramework.Infrastructure.Jobs.Scheduling;

/// <summary>
/// Makes the store match the code: one durable job and one cron trigger per scheduled job, nothing
/// else in the <see cref="JobSchedules.Group"/> group. Runs once, at worker startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>An unchanged cron leaves its trigger completely untouched.</b> <c>RescheduleJob</c> always
/// stores a freshly built trigger, whose next-fire-time is the next FUTURE occurrence — rewriting a
/// trigger whose cron has not actually changed would silently discard any misfire this worker was
/// meant to catch up on (the whole point of <see cref="CronTriggerMisfireInstruction.FireAndProceed"/>),
/// and would briefly reset an operator-paused trigger to <see cref="TriggerState.Normal"/> between
/// the reschedule and the later <c>PauseTrigger</c> call, letting another node still running an
/// older pod acquire and fire it mid-rollout. So the existing trigger is fetched first, and is
/// rebuilt only when its cron or misfire instruction actually differs from what the code now wants.
/// </para>
/// <para>
/// <b>Residual gap: a genuine cron change still has a pause window.</b> When the cron in code has
/// really changed, the trigger must be rebuilt, and <c>RescheduleJob</c> stores the replacement in
/// the <see cref="TriggerState.Normal"/> state; the later <c>PauseTrigger</c> call that restores a
/// pause is necessarily separate. A paused trigger whose cron is edited in the same deploy is
/// therefore briefly unpaused in between — the one case where this class's "pause survives a deploy"
/// guarantee does not fully hold, narrowed from "every startup" to "the one startup that changes
/// that job's cron." Closing it fully would need an atomic reschedule-and-pause the current API does
/// not expose. With several workers starting together, Quartz's cluster locks serialise the writes
/// and — provided they run the same build — every node computes and writes the same values. Two
/// nodes starting together in that cron-changing deploy can still lose a pause for good: B reads
/// the state as Normal inside A's unpaused window, then reschedules after A has re-paused.
/// </para>
/// <para>
/// <b>Mixed builds do not agree.</b> The store holds the schedules of whichever build synchronized
/// last. During a rollout, an old-build pod that restarts after the new pods have started deletes
/// any job only the new build schedules, and reverts any cron the new build changed; the reverse
/// re-adds a job the new build removed, which then throws on every firing. It lasts until the next
/// new-build worker starts, so the remedy is to restart a worker once the rollout has finished.
/// Version-stamping the entries was rejected: it would also stop a rollback from removing what the
/// rolled-back build no longer schedules, which is the one mixed-build case that is correct today.
/// </para>
/// </remarks>
public sealed partial class ScheduleSynchronizer(
    ISchedulerFactory schedulers,
    IOptions<JobOptions> options,
    ILogger<ScheduleSynchronizer> logger)
{
    public async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        var scheduler = await schedulers.GetScheduler(cancellationToken).ConfigureAwait(false);
        var scheduled = JobRegistration.Jobs.Where(j => j.IsScheduled).ToArray();

        foreach (var job in scheduled)
        {
            await UpsertAsync(scheduler, job, JobSchedules.EffectiveCron(job, options.Value), cancellationToken)
                .ConfigureAwait(false);
        }

        await RemoveStaleAsync(scheduler, scheduled, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertAsync(IScheduler scheduler, JobDescriptor job, string cron, CancellationToken ct)
    {
        var jobKey = JobSchedules.JobKeyFor(job);
        var triggerKey = JobSchedules.TriggerKeyFor(job);

        // AddJob(..., AddJobOptions.Replacing) only ever stores/overwrites the IJobDetail — its own
        // Quartz.xml doc says it adds a job "with no associated ITrigger", and nothing in IScheduler
        // suggests otherwise. It never touches this job's existing trigger(s), so it is always safe
        // to run unconditionally, before the unchanged-trigger check below.
        await scheduler.AddJob(
                JobBuilder.Create<EnqueueScheduledJob>()
                    .WithIdentity(jobKey)
                    .UsingJobData(JobSchedules.JobNameKey, job.Name)
                    .StoreDurably()
                    .Build(),
                AddJobOptions.Replacing,
                ct)
            .ConfigureAwait(false);

        var existing = await scheduler.GetTrigger(triggerKey, ct).ConfigureAwait(false);
        var wasPaused = await scheduler.GetTriggerState(triggerKey, ct).ConfigureAwait(false) == TriggerState.Paused;

        if (existing is ICronTrigger existingCron && IsUnchanged(existingCron, cron))
        {
            // Same cron, same misfire instruction: leave the trigger completely untouched. See the
            // class remarks for why replacing it anyway — even with an identical cron — would lose
            // a pending misfire catch-up and open a pause gap on every single startup.
            LogScheduled(logger, job.Name, cron, wasPaused);
            return;
        }

        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .WithCronSchedule(cron, b => b.WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed))
            .Build();

        if (existing is null)
        {
            await scheduler.ScheduleJob(trigger, ScheduleJobOptions.Replacing, ct).ConfigureAwait(false);
        }
        else
        {
            await scheduler.RescheduleJob(triggerKey, trigger, ct).ConfigureAwait(false);
        }

        if (wasPaused)
        {
            await scheduler.PauseTrigger(triggerKey, ct).ConfigureAwait(false);
        }

        LogScheduled(logger, job.Name, cron, wasPaused);
    }

    /// <summary>
    /// Whether <paramref name="existing"/> already says what <paramref name="cron"/> and this
    /// synchronizer's fixed <see cref="CronTriggerMisfireInstruction.FireAndProceed"/> say — the
    /// only two things this synchronizer ever sets on a trigger. Time zone is deliberately not
    /// compared: the trigger this class builds never calls <c>CronScheduleBuilder.InTimeZone</c>,
    /// so every trigger it has ever stored resolves its cron in the same (default) zone, and there
    /// is nothing here that could make the two disagree.
    /// </summary>
    private static bool IsUnchanged(ICronTrigger existing, string cron) =>
        string.Equals(existing.CronExpressionString, cron, StringComparison.Ordinal) &&
        existing.MisfireInstruction == CronTriggerMisfireInstruction.FireAndProceed;

    private async Task RemoveStaleAsync(IScheduler scheduler, JobDescriptor[] scheduled, CancellationToken ct)
    {
        var wanted = scheduled.Select(j => j.Name).ToHashSet(StringComparer.Ordinal);
        var stale = new List<JobKey>();

        // Paged: one page is enough for any realistic job count, but reading HasMore means a large
        // store cannot silently leave stale jobs behind.
        var query = new JobQuery { Group = GroupMatcher<JobKey>.GroupEquals(JobSchedules.Group) };
        var page = await scheduler.QueryJobs(query, ct).ConfigureAwait(false);
        stale.AddRange(page.Items.Select(h => h.Key).Where(k => !wanted.Contains(k.Name)));

        if (page.HasMore)
        {
            throw new InvalidOperationException(
                $"More jobs in the '{JobSchedules.Group}' group than one page holds; the synchronizer " +
                "needs to page through them before it can safely remove stale ones.");
        }

        foreach (var key in stale)
        {
            // Nodes starting together all see the same stale job; only the one whose delete
            // actually removed it logs, so one removal is one Warning.
            if (await scheduler.DeleteJob(key, ct).ConfigureAwait(false))
            {
                LogRemoved(logger, key.Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Schedule {JobName} = '{Cron}' (paused: {Paused}).")]
    private static partial void LogScheduled(ILogger logger, string jobName, string cron, bool paused);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Removed schedule {JobName}: no longer a scheduled job in this build.")]
    private static partial void LogRemoved(ILogger logger, string jobName);
}
