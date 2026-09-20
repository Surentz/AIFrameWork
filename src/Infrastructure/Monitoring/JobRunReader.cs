using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// Reads <c>job_runs</c> for the monitoring page. Every read is <c>AsNoTracking</c> and projected:
/// nothing here is followed by a write, and a tracked entity would be scanned by
/// <c>SaveChanges</c> and <c>DomainEventsInterceptor</c> on any later write in the same scope.
/// </summary>
internal sealed class JobRunReader(AiFrameworkDbContext context) : IJobRunReader
{
    public async Task<JobRunPage> ListAsync(
        JobRunStatus? status,
        string? jobName,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = context.JobRuns.AsNoTracking();

        if (status is { } wanted)
        {
            query = query.Where(run => run.Status == wanted);
        }

        if (!string.IsNullOrWhiteSpace(jobName))
        {
            query = query.Where(run => run.JobName == jobName);
        }

        // Counted before paging, so the page can say "3 of 412" rather than "3 of 20".
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            // Attempt breaks the tie: two attempts of the same job can share a StartedAt to the
            // microsecond under a fast retry, and an unstable sort would shuffle them between
            // polls of a page that refreshes every ten seconds.
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Attempt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(run => new JobRunView(
                run.EnvelopeId,
                run.Attempt,
                run.JobName,
                run.Lane,
                run.Status,
                run.StartedAt,
                run.CompletedAt,
                run.DurationMs,
                run.OwnerId,
                run.Error,
                run.TraceId,
                run.InstanceId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new JobRunPage(items, total, page);
    }

    public Task<JobRunView?> GetAsync(
        Guid envelopeId, int attempt, CancellationToken cancellationToken) =>
        context.JobRuns
            .AsNoTracking()
            .Where(run => run.EnvelopeId == envelopeId && run.Attempt == attempt)
            .Select(run => new JobRunView(
                run.EnvelopeId,
                run.Attempt,
                run.JobName,
                run.Lane,
                run.Status,
                run.StartedAt,
                run.CompletedAt,
                run.DurationMs,
                run.OwnerId,
                run.Error,
                run.TraceId,
                run.InstanceId))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Grouped in the database, not in memory: the whole point of a tile is that it costs one
    /// query rather than one row per run in the window.
    /// </summary>
    public async Task<IReadOnlyDictionary<JobRunStatus, int>> CountByStatusAsync(
        DateTimeOffset since, CancellationToken cancellationToken)
    {
        var counts = await context.JobRuns
            .AsNoTracking()
            .Where(run => run.StartedAt >= since)
            .GroupBy(run => run.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return counts.ToDictionary(entry => entry.Status, entry => entry.Count);
    }
}
