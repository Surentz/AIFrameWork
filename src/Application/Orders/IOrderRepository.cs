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

    /// <summary>
    /// Any buyer's order, TRACKED, for the fulfilment path — the operator shipping it. Null only
    /// when no order has this id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT scoped to an owner, and named for its one purpose rather than as an
    /// overload of <see cref="GetForUpdateAsync"/>: ADR 0007 keeps ownership in these signatures
    /// so a caller cannot forget to filter, and the way to keep that true while adding a
    /// cross-owner read is a method whose name says so at the call site. Only a handler behind the
    /// <c>Orders.Fulfil</c> policy calls it. See ADR 0024.
    /// </para>
    /// <para>
    /// Tracked for the reason <see cref="GetForUpdateAsync"/> gives.
    /// </para>
    /// </remarks>
    public Task<Order?> GetForFulfilmentAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Every buyer's orders in <paramref name="status"/>, OLDEST first, one keyset page at a time,
    /// with each buyer's username. The fulfilment queue.
    /// </summary>
    /// <remarks>
    /// Oldest first because a fulfilment queue is FIFO: the order that has waited longest is the
    /// next one to ship. <paramref name="after"/> is therefore the last row of the previous page,
    /// and the next page is everything strictly LATER than it. Cross-owner for the reason
    /// <see cref="GetForFulfilmentAsync"/> gives.
    /// </remarks>
    public Task<IReadOnlyList<FulfilmentQueueRow>> ListForFulfilmentAsync(
        OrderStatus status,
        int limit,
        (DateTimeOffset PlacedAt, Guid Id)? after,
        CancellationToken cancellationToken);
}

/// <summary>
/// A projection of one order for the fulfilment queue — never the entity, so a read that only
/// displays cannot accidentally become a write.
/// </summary>
/// <remarks>
/// <c>BuyerUsername</c> is null only if the order's user row does not exist. Nothing deletes users
/// today and there is no foreign key between the tables, so the read is a LEFT join: a missing
/// buyer must not make an order vanish from the queue that is supposed to ship it.
/// </remarks>
public sealed record FulfilmentQueueRow(
    Guid Id,
    Guid BuyerId,
    string? BuyerUsername,
    string Sku,
    int Quantity,
    DateTimeOffset PlacedAt,
    string? ProductName,
    decimal? UnitPrice,
    OrderStatus Status);
