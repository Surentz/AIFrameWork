using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;

namespace AiFramework.Application.Monitoring;

/// <summary>One recorded administrative change, as the screen reads it.</summary>
public sealed record AdminActionView(
    Guid Id,
    DateTimeOffset At,
    AdminActionKind Kind,
    Guid ActorUserId,
    string ActorUsername,
    Guid TargetUserId,
    string TargetUsername,
    string? IpAddress,
    string? TraceId);

public sealed record AdminActionPage(
    IReadOnlyList<AdminActionView> Items, int TotalCount, int Page);

/// <summary>
/// Reads the administrative audit. See ADR 0022.
/// </summary>
public interface IAdminActionReader
{
    /// <summary>
    /// Newest first. <paramref name="targetUserId"/> null returns every account's history, which
    /// is what the overview timeline reads.
    /// </summary>
    public Task<AdminActionPage> ListAsync(
        Guid? targetUserId, int page, int pageSize, CancellationToken cancellationToken);
}

/// <summary>
/// What has been done to one account, or to every account.
/// </summary>
/// <remarks>
/// <b>Not <c>ICacheable</c></b>, for the reason <c>ListUsers</c> is not: this is the page an
/// administrator opens immediately after acting, and a cached one would read as the action
/// having failed.
/// </remarks>
public sealed record GetUserActions(Guid? TargetUserId, int Page, int PageSize)
    : IQuery<AdminActionPage>;

public sealed class GetUserActionsHandler(IAdminActionReader reader)
    : IQueryHandler<GetUserActions, AdminActionPage>
{
    private const int MaxPageSize = 100;

    public async Task<Result<AdminActionPage>> HandleAsync(
        GetUserActions query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var result = await reader
            .ListAsync(query.TargetUserId, page, size, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(result);
    }
}
