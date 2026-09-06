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
    public Task<Order?> GetAsync(Guid id, Guid ownerId, CancellationToken cancellationToken);

    public Task<IReadOnlyList<Order>> ListAsync(
        Guid ownerId,
        int limit,
        (DateTimeOffset PlacedAt, Guid Id)? after,
        CancellationToken cancellationToken);
}
