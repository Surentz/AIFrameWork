using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class OrderRepository(AiFrameworkDbContext context) : IOrderRepository
{
    public async Task AddAsync(Order order, CancellationToken cancellationToken) =>
        await context.Orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

    public Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
}
