using AiFramework.Domain.Notifications;

namespace AiFramework.Application.Notifications;

public interface INotificationRepository
{
    public Task AddAsync(Notification notification, CancellationToken cancellationToken);

    /// <summary>
    /// Scoped to the owner by signature, exactly like <see cref="Orders.IOrderRepository.GetAsync"/>:
    /// there is no overload that returns another user's notification, so a caller cannot forget
    /// to filter. One belonging to somebody else comes back null, the same as one that does not
    /// exist — which is also what stops this endpoint confirming that an id is real to a caller
    /// who does not own it.
    ///
    /// TRACKED, because its only caller marks the result read. See
    /// <see cref="Orders.IOrderRepository.GetForUpdateAsync"/> for why tracking is named rather
    /// than passed as a flag.
    /// </summary>
    public Task<Notification?> GetForUpdateAsync(
        Guid id, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// Newest first, keyset-paged on (CreatedAt, Id) like
    /// <see cref="Orders.IOrderRepository.ListAsync"/>.
    ///
    /// <c>unreadOnly</c> filters in SQL rather than in the handler: a user with a long history
    /// could otherwise page through thousands of read rows to reach the handful of unread ones.
    /// </summary>
    public Task<IReadOnlyList<Notification>> ListAsync(
        Guid ownerId,
        bool unreadOnly,
        int limit,
        (DateTimeOffset CreatedAt, Guid Id)? after,
        CancellationToken cancellationToken);

    /// <summary>
    /// Counted in SQL rather than by listing and counting in memory — the badge on a client's
    /// bell icon is the most-called read in this feature and must not materialize rows.
    /// </summary>
    public Task<int> CountUnreadAsync(Guid ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// Every unread notification for this user, TRACKED, for <c>MarkAllRead</c> to mark. Returns
    /// the entities rather than issuing a set-based UPDATE so that
    /// <see cref="Notification.MarkRead"/> stays the single place read-ness is decided — a bulk
    /// <c>ExecuteUpdateAsync</c> would write the column behind the aggregate's back, and the
    /// first-write-wins rule with it.
    /// </summary>
    public Task<IReadOnlyList<Notification>> ListUnreadForUpdateAsync(
        Guid ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// Who this outbox message has already notified. The dedupe check that makes every event
    /// handler idempotent: delivery is at-least-once, so each of them WILL run twice eventually.
    /// </summary>
    /// <remarks>
    /// Returns the whole recipient set in ONE query rather than offering a per-user
    /// "does this exist" — a price change fans out to every purchaser, and asking once per
    /// candidate would issue a query per recipient on the retry path, which is where the system
    /// is already under strain. Single-recipient handlers use the same method and just test
    /// <c>Contains</c>.
    ///
    /// This check is an optimization, not the guarantee. Two concurrent deliveries of the same
    /// message can both pass it and both insert; the unique index on (SourceMessageId, UserId) is
    /// what actually prevents a duplicate landing in someone's feed, at the cost of failing one
    /// of the two deliveries, which the outbox then retries into a no-op.
    /// </remarks>
    public Task<IReadOnlySet<Guid>> ListNotifiedRecipientsAsync(
        Guid sourceMessageId, CancellationToken cancellationToken);
}
