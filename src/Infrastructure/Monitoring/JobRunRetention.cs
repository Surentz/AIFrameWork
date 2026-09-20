using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// Deletes job-run rows past retention with one set-based statement.
/// </summary>
/// <remarks>
/// <c>ExecuteDeleteAsync</c> rather than loading and removing entities: this runs against a table
/// whose whole problem is being large, and materialising the rows about to be deleted is the one
/// way to make a cheap sweep expensive. It also bypasses the change tracker entirely, so the sweep
/// cannot accidentally commit anything else in the scope.
/// </remarks>
internal sealed class JobRunRetention(
    AiFrameworkDbContext context, IOptions<MonitoringOptions> options, IClock clock)
    : IJobRunRetention
{
    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-options.Value.JobRunRetentionDays);

        // On StartedAt, not CompletedAt: a run that never completed - the worker died holding it -
        // has a null CompletedAt and would otherwise be kept forever, which is precisely backwards.
        return context.JobRuns
            .Where(run => run.StartedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
