using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;

namespace AiFramework.Application.Monitoring;

/// <summary>
/// The sign-in audit trail. Never <c>ICacheable</c>: ADR 0008's account state must be read every
/// time, and this is the one page where a stale answer is a security answer.
/// </summary>
public sealed record GetSignInEvents(SignInOutcome? Outcome, string? Username, int Page, int PageSize)
    : IQuery<SignInEventPage>;

public sealed class GetSignInEventsHandler(ISignInEventReader events)
    : IQueryHandler<GetSignInEvents, SignInEventPage>
{
    public async Task<Result<SignInEventPage>> HandleAsync(
        GetSignInEvents query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Page < 1)
        {
            return Result.Failure<SignInEventPage>(new Error(
                ErrorKind.Validation, "monitoring.page_invalid", "Page must be 1 or greater."));
        }

        if (query.PageSize is < 1 or > GetJobRunsHandler.MaxPageSize)
        {
            return Result.Failure<SignInEventPage>(new Error(
                ErrorKind.Validation,
                "monitoring.page_size_invalid",
                $"Page size must be between 1 and {GetJobRunsHandler.MaxPageSize}."));
        }

        var page = await events
            .ListAsync(query.Outcome, query.Username, query.Page, query.PageSize, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(page);
    }
}
