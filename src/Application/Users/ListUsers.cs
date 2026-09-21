using AiFramework.Application.Abstractions;
using AiFramework.Domain.Users;

namespace AiFramework.Application.Users;

/// <summary>
/// One account, as the user-management screen reads it.
/// </summary>
/// <remarks>
/// <c>RoleIsConfigured</c> says whether this username appears in <c>Admin__Usernames</c>. It is
/// not a role and is not persisted: configuration is a floor (ADR 0022), so an account demoted
/// here while still named there is promoted straight back at the next API start. The field is
/// what lets the screen say so BEFORE somebody demotes them, rather than leaving them to find
/// out when a restart undoes their work.
/// </remarks>
public sealed record AdministeredUserView(
    Guid Id,
    string Username,
    string DisplayName,
    UserRole Role,
    bool RoleIsConfigured,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastSeenAt);

public sealed record AdministeredUserPage(
    IReadOnlyList<AdministeredUserView> Items, int TotalCount, int Page);

/// <summary>
/// The accounts, newest-seen first, optionally narrowed by a username or display-name fragment.
/// </summary>
/// <remarks>
/// <b>Deliberately NOT <c>ICacheable</c>, permanently.</b> This reports who holds privilege, and
/// a stale answer about privilege is a security bug rather than a stale read — the same posture
/// the whole auth path takes (ADR 0008, ADR 0009). It is also the page an administrator reloads
/// immediately after making a change, so a cached one would read as the change having failed.
/// </remarks>
public sealed record ListUsers(string? Search, int Page, int PageSize)
    : IQuery<AdministeredUserPage>;

public sealed class ListUsersHandler(IUserRepository users, IAdministratorDirectory administrators)
    : IQueryHandler<ListUsers, AdministeredUserPage>
{
    private const int MaxPageSize = 100;

    public async Task<Result<AdministeredUserPage>> HandleAsync(
        ListUsers query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Validated in the handler rather than by a validator, the same way GetOrders does it:
        // queries get no validation behavior, and a page size of zero is the caller being wrong
        // rather than a system fault.
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var (rows, total) = await users
            .ListAsync(query.Search, page, size, cancellationToken)
            .ConfigureAwait(false);

        // Matched in memory against the configured list, NOT joined in the query: AdminOptions is
        // a configuration array rather than a table, so there is nothing for the database to join
        // to. One list, read once, matched against this page's rows only.
        var items = rows
            .Select(row => new AdministeredUserView(
                row.Id,
                row.Username,
                row.DisplayName,
                row.Role,
                administrators.IsAdministrator(row.Username),
                row.RegisteredAt,
                row.LastSeenAt))
            .ToList();

        return Result.Success(new AdministeredUserPage(items, total, page));
    }
}
