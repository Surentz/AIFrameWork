using AiFramework.Application.Notifications;
using AiFramework.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class NotificationRepository(AiFrameworkDbContext context) : INotificationRepository
{
    public async Task AddAsync(Notification notification, CancellationToken cancellationToken) =>
        await context.Notifications.AddAsync(notification, cancellationToken).ConfigureAwait(false);

    // Tracked deliberately — no AsNoTracking here. Its only caller marks the result read, and
    // the unit-of-work behavior has to find a tracked entity to save. See the port's remarks.
    public Task<Notification?> GetForUpdateAsync(
        Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        context.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == ownerId, cancellationToken);

    public async Task<IReadOnlyList<Notification>> ListAsync(
        Guid ownerId,
        bool unreadOnly,
        int limit,
        (DateTimeOffset CreatedAt, Guid Id)? after,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // The owner filter stays in SQL as the leading column of
        // IX_Notifications_UserId_CreatedAt_Id_Desc; filtering a fetched page in memory would
        // break the keyset the same way it would for orders.
        var query = context.Notifications.AsNoTracking().Where(n => n.UserId == ownerId);

        if (unreadOnly)
        {
            // Matches IX_Notifications_Unread's filter predicate exactly, so Postgres can use
            // the partial index rather than the full one.
            query = query.Where(n => n.ReadAt == null);
        }

        if (after is { } cursor)
        {
            // Keyset, not offset — see OrderRepository.ListAsync for why, and for why
            // n.Id.CompareTo runs as Postgres's own uuid comparison rather than in CLR code.
            query = query.Where(n => n.CreatedAt < cursor.CreatedAt
                || (n.CreatedAt == cursor.CreatedAt && n.Id.CompareTo(cursor.Id) < 0));
        }

        return await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<int> CountUnreadAsync(Guid ownerId, CancellationToken cancellationToken) =>
        context.Notifications
            .CountAsync(n => n.UserId == ownerId && n.ReadAt == null, cancellationToken);

    // Tracked, like GetForUpdateAsync and for the same reason: MarkAllRead mutates every row
    // this returns.
    public async Task<IReadOnlyList<Notification>> ListUnreadForUpdateAsync(
        Guid ownerId, CancellationToken cancellationToken) =>
        await context.Notifications
            .Where(n => n.UserId == ownerId && n.ReadAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlySet<Guid>> ListNotifiedRecipientsAsync(
        Guid sourceMessageId, CancellationToken cancellationToken)
    {
        // Projects to the id before materializing, so a message that fanned out to hundreds of
        // recipients does not hydrate hundreds of entities just to answer "who already has one".
        var recipients = await context.Notifications
            .AsNoTracking()
            .Where(n => n.SourceMessageId == sourceMessageId)
            .Select(n => n.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return recipients.ToHashSet();
    }
}
