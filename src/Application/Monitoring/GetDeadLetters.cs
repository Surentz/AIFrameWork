using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>Messages that exhausted their retries. Never cached, like everything here.</summary>
public sealed record GetDeadLetters(int Page, int PageSize) : IQuery<DeadLetterPage>;

public sealed class GetDeadLettersHandler(IDeadLetterStore deadLetters)
    : IQueryHandler<GetDeadLetters, DeadLetterPage>
{
    public async Task<Result<DeadLetterPage>> HandleAsync(
        GetDeadLetters query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Page < 1)
        {
            return Result.Failure<DeadLetterPage>(new Error(
                ErrorKind.Validation, "monitoring.page_invalid", "Page must be 1 or greater."));
        }

        if (query.PageSize is < 1 or > GetJobRunsHandler.MaxPageSize)
        {
            return Result.Failure<DeadLetterPage>(new Error(
                ErrorKind.Validation,
                "monitoring.page_size_invalid",
                $"Page size must be between 1 and {GetJobRunsHandler.MaxPageSize}."));
        }

        var page = await deadLetters.ListAsync(query.Page, query.PageSize, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(page);
    }
}
