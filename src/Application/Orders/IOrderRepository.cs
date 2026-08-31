using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

public interface IOrderRepository
{
    public Task AddAsync(Order order, CancellationToken cancellationToken);

    public Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken);
}
