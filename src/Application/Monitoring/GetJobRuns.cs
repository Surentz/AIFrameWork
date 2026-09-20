using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>
/// The job-run table. Deliberately NOT <c>ICacheable</c>, permanently: this is the current state
/// of the system, and a thirty-second stale answer to "is it broken right now" is worse than no
/// answer. ADR 0021.
/// </summary>
public sealed record GetJobRuns(JobRunStatus? Status, string? JobName, int Page, int PageSize)
    : IQuery<JobRunPage>;

public sealed class GetJobRunsHandler(IJobRunReader runs) : IQueryHandler<GetJobRuns, JobRunPage>
{
    /// <summary>The largest page this endpoint will serve, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    public async Task<Result<JobRunPage>> HandleAsync(
        GetJobRuns query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Validated in the handler rather than by a validator, the same split GetOrdersHandler
        // uses: the query path has no validation behavior, by design.
        if (query.Page < 1)
        {
            return Result.Failure<JobRunPage>(new Error(
                ErrorKind.Validation, "monitoring.page_invalid", "Page must be 1 or greater."));
        }

        if (query.PageSize is < 1 or > MaxPageSize)
        {
            return Result.Failure<JobRunPage>(new Error(
                ErrorKind.Validation,
                "monitoring.page_size_invalid",
                $"Page size must be between 1 and {MaxPageSize}."));
        }

        var page = await runs
            .ListAsync(query.Status, query.JobName, query.Page, query.PageSize, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(page);
    }
}
