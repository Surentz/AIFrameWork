using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>Counts by outcome over a trailing window, for the overview tiles.</summary>
public sealed record GetJobHealth(TimeSpan Window) : IQuery<JobHealthView>;

public sealed class GetJobHealthHandler(
    IJobRunReader runs, IDeadLetterStore deadLetters, IClock clock)
    : IQueryHandler<GetJobHealth, JobHealthView>
{
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(30);

    public async Task<Result<JobHealthView>> HandleAsync(
        GetJobHealth query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Window <= TimeSpan.Zero || query.Window > MaxWindow)
        {
            return Result.Failure<JobHealthView>(new Error(
                ErrorKind.Validation,
                "monitoring.window_invalid",
                $"The window must be positive and no more than {MaxWindow.TotalDays} days."));
        }

        var since = clock.UtcNow - query.Window;

        var counts = await runs.CountByStatusAsync(since, cancellationToken).ConfigureAwait(false);

        // The dead-letter count is the whole queue rather than the window: a message that has
        // been stuck for a week is exactly the one an operator needs to see, and windowing it
        // would hide the oldest failures behind the newest.
        var dead = await deadLetters.ListAsync(page: 1, pageSize: 1, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(new JobHealthView(
            Running: counts.GetValueOrDefault(JobRunStatus.Running),
            Succeeded: counts.GetValueOrDefault(JobRunStatus.Succeeded),
            Failed: counts.GetValueOrDefault(JobRunStatus.Failed),
            DeadLettered: dead.TotalCount,
            Since: since));
    }
}
