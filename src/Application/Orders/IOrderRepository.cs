using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

public interface IOrderRepository
{
    public Task AddAsync(Order order, CancellationToken cancellationToken);

    /// <summary>
    /// Scoped to the owner by signature: there is no overload that returns another user's
    /// order, so a caller cannot forget to filter. An order the caller does not own comes
    /// back null, exactly like one that does not exist.
    /// </summary>
    /// <remarks>
    /// Reads UNTRACKED. Mutating what this returns changes nothing — the unit-of-work behavior
    /// has no tracked entity to save, so the write is lost silently, with no exception and no
    /// failing test anywhere near it. A handler that intends to change the order wants
    /// <see cref="GetForUpdateAsync"/> instead.
    /// </remarks>
    public Task<Order?> GetAsync(Guid id, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// The same owner-scoped read as <see cref="GetAsync"/>, but TRACKED, for handlers that go on
    /// to call a state-changing method like <see cref="Order.Ship"/>.
    /// </summary>
    /// <remarks>
    /// Two methods rather than a bool parameter, because the distinction decides whether a write
    /// happens at all, and a caller that gets it wrong gets no feedback. A name at the call site
    /// is the difference being visible in code review; <c>tracked: false</c> is not.
    /// </remarks>
    public Task<Order?> GetForUpdateAsync(Guid id, Guid ownerId, CancellationToken cancellationToken);

    public Task<IReadOnlyList<Order>> ListAsync(
        Guid ownerId,
        int limit,
        (DateTimeOffset PlacedAt, Guid Id)? after,
        CancellationToken cancellationToken);

    /// <summary>
    /// Who placed this order, or null if there is no such order.
    /// </summary>
    /// <remarks>
    /// The one method here NOT scoped to an owner, which is the point of it: it answers "who owns
    /// this", so it cannot take the answer as an argument. Safe precisely because it returns an
    /// owner id and nothing else — no order state leaks through it.
    ///
    /// It exists for the outbox pump, which has no caller to scope to. <c>OrderPlaced</c> carries
    /// no UserId (it predates the notification feed, and its payload is already persisted in
    /// outbox rows that deserialize by name into the old shape), so a handler that needs a
    /// recipient has to look one up. The later lifecycle events carry UserId and need no lookup —
    /// see <c>OrderShipped</c>.
    /// </remarks>
    public Task<Guid?> GetOwnerAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Distinct users who have ordered this sku, most recent purchase first, capped at
    /// <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// The recipient rule for a price change, and a deliberately BOUNDED one. A popular product
    /// could have been ordered by every user in the system, and one outbox message is a poor
    /// place to fan out unboundedly: the whole batch shares a transaction, and retry granularity
    /// is the message, so a failure part-way replays all of it. Capping trades completeness for a
    /// predictable worst case — the users who bought it least recently are the ones dropped.
    /// Revisit when there is a real subscription model to replace "everyone who ever bought it".
    /// </remarks>
    public Task<IReadOnlyList<Guid>> ListPurchaserIdsAsync(
        string sku, int limit, CancellationToken cancellationToken);
}
