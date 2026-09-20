using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>One attempt, with its full error text. Keyed as the row is, on the pair.</summary>
public sealed record GetJobRun(Guid EnvelopeId, int Attempt) : IQuery<JobRunView>;

public sealed class GetJobRunHandler(IJobRunReader runs) : IQueryHandler<GetJobRun, JobRunView>
{
    public async Task<Result<JobRunView>> HandleAsync(
        GetJobRun query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var run = await runs.GetAsync(query.EnvelopeId, query.Attempt, cancellationToken)
            .ConfigureAwait(false);

        return run is null
            ? Result.Failure<JobRunView>(new Error(
                ErrorKind.NotFound,
                "job_run.not_found",
                $"No attempt {query.Attempt} of job run '{query.EnvelopeId}'."))
            : Result.Success(run);
    }
}
