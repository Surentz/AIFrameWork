using AiFramework.Application.Abstractions;
using AiFramework.Domain.Notifications;

namespace AiFramework.Application.Notifications;

public sealed record NotificationListItem(
    Guid Id,
    NotificationKind Kind,
    string Title,
    string Body,
    Guid? SubjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

public sealed record NotificationPage(
    IReadOnlyList<NotificationListItem> Items, string? NextCursor);

/// <summary>
/// The feed, newest first.
/// </summary>
/// <remarks>
/// Deliberately NOT <c>ICacheable</c>, unlike <c>GetOrders</c> and <c>GetProducts</c>. Two
/// independent reasons, either one sufficient:
///
/// 1. Eviction could never fire. <c>IInvalidatesCache</c> is scoped to the CALLING user, and
///    nothing a caller does creates their own notifications — the outbox pump does, on an
///    arbitrary pod, with no current user at all. There is no command this query could hang an
///    eviction tag off, so a cached feed would simply go stale for the whole TTL with no way to
///    clear it.
/// 2. It would contradict the realtime push. A client told over SignalR that something arrived,
///    which then re-reads the feed and does not see it, is worse than one that was never told.
///
/// This is the same "permanently, not for now" posture the auth path takes (root CLAUDE.md), and
/// for a comparable reason: the read has to be current, so the cache is the wrong tool rather
/// than a tool that is merely switched off.
/// </remarks>
public sealed record GetNotifications(int Limit, string? Cursor, bool UnreadOnly)
    : IQuery<NotificationPage>;

/// <summary>
/// Validates its own inputs, because <c>QueryDispatcher</c> does not run the validation behavior
/// — that is command-only. Mirrors <c>GetOrdersHandler</c>.
/// </summary>
public sealed class GetNotificationsHandler(
    INotificationRepository notifications, ICurrentUser currentUser)
    : IQueryHandler<GetNotifications, NotificationPage>
{
    private const int MaxLimit = 100;

    public async Task<Result<NotificationPage>> HandleAsync(
        GetNotifications query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<NotificationPage>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        if (query.Limit is < 1 or > MaxLimit)
        {
            return Result.Failure<NotificationPage>(new Error(
                ErrorKind.Validation, "notifications.limit_out_of_range",
                $"Limit must be between 1 and {MaxLimit}."));
        }

        (DateTimeOffset CreatedAt, Guid Id)? after = null;
        if (query.Cursor is not null)
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var decoded))
            {
                return Result.Failure<NotificationPage>(new Error(
                    ErrorKind.Validation, "notifications.malformed_cursor",
                    "The cursor could not be parsed."));
            }

            after = decoded;
        }

        // One more than asked for, so "is there a next page" needs no second COUNT.
        var rows = await notifications
            .ListAsync(userId, query.UnreadOnly, query.Limit + 1, after, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit)
            .Select(n => new NotificationListItem(
                n.Id, n.Kind, n.Title, n.Body, n.SubjectId, n.CreatedAt, n.ReadAt))
            .ToArray();

        var next = hasMore
            ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id)
            : null;

        return Result.Success(new NotificationPage(page, next));
    }
}
