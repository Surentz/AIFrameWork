using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace AiFramework.Infrastructure.Jobs.Scheduling;

/// <summary>
/// Makes the store match the code: one durable job and one cron trigger per scheduled job, nothing
/// else in the <see cref="JobSchedules.Group"/> group. Runs once, at worker startup.
/// </summary>
/// <remarks>
/// <b>Never changes a trigger's paused state.</b> A pause (piece 5's monitoring page) must survive
/// redeploys, so a paused trigger whose cron is updated is paused again immediately. With several
/// workers starting together, Quartz's cluster locks serialise the writes and every node writes the
/// same values.
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

        await scheduler.AddJob(
                JobBuilder.Create<EnqueueScheduledJob>()
                    .WithIdentity(jobKey)
                    .UsingJobData(JobSchedules.JobNameKey, job.Name)
                    .StoreDurably()
                    .Build(),
                AddJobOptions.Replacing,
                ct)
            .ConfigureAwait(false);

        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(jobKey)
            .WithCronSchedule(cron, b => b.WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed))
            .Build();

        var wasPaused = await scheduler.GetTriggerState(triggerKey, ct).ConfigureAwait(false) == TriggerState.Paused;

        if (await scheduler.GetTrigger(triggerKey, ct).ConfigureAwait(false) is null)
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
            await scheduler.DeleteJob(key, ct).ConfigureAwait(false);
            LogRemoved(logger, key.Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Schedule {JobName} = '{Cron}' (paused: {Paused}).")]
    private static partial void LogScheduled(ILogger logger, string jobName, string cron, bool paused);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Removed schedule {JobName}: no longer a scheduled job in this build.")]
    private static partial void LogRemoved(ILogger logger, string jobName);
}
