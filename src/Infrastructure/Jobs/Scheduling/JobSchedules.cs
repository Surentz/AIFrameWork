using Quartz;

namespace AiFramework.Infrastructure.Jobs.Scheduling;

/// <summary>
/// Everything about a schedule that does not need a running scheduler: keys, the effective cron,
/// and cron validation. Kept apart from <see cref="ScheduleSynchronizer"/> so it is testable with
/// no host and no database.
/// </summary>
public static class JobSchedules
{
    /// <summary>
    /// The only Quartz group this code ever reads or writes. The synchronizer deletes stale entries
    /// within it and nowhere else, so nothing else a store might hold is ever at risk.
    /// </summary>
    public const string Group = "jobs";

    /// <summary>The JobDataMap key carrying the job's <see cref="JobDescriptor.Name"/>.</summary>
    public const string JobNameKey = "job";

    public static JobKey JobKeyFor(JobDescriptor job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new JobKey(job.Name, Group);
    }

    public static TriggerKey TriggerKeyFor(JobDescriptor job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new TriggerKey(job.Name, Group);
    }

    /// <summary>
    /// Quartz's own parser, so "valid" means exactly what the scheduler will accept — seconds
    /// first (<c>"0 5 * * * ?"</c>).
    /// </summary>
    public static bool IsValidCron(string cron) =>
        !string.IsNullOrWhiteSpace(cron) && CronExpression.TryParse(cron, out _);

    /// <summary>The config override if there is one, otherwise the code default.</summary>
    public static string EffectiveCron(JobDescriptor job, JobOptions options)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(options);

        if (!job.IsScheduled)
        {
            throw new InvalidOperationException($"{job.Name} is not a scheduled job.");
        }

        return options.Schedules.TryGetValue(job.Name, out var overridden)
            ? overridden
            : job.DefaultCron!;
    }

    public static JobDescriptor? Find(string name) =>
        JobRegistration.Jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.Ordinal));
}
