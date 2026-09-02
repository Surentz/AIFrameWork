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

    public async Task<IReadOnlyList<Order>> ListAsync(
        int limit, (DateTimeOffset PlacedAt, Guid Id)? after, CancellationToken cancellationToken)
    {
        var query = context.Orders.AsNoTracking();

        if (after is { } cursor)
        {
            // Keyset, not offset: a row inserted while a caller sits on page 1 must not make
            // page 2 repeat what page 1 already showed. Both the comparison and the ordering
            // run in SQL - Postgres orders uuid byte-wise and .NET's Guid.CompareTo does not,
            // so re-sorting a page in memory would break the agreement.
            query = query.Where(o => o.PlacedAt < cursor.PlacedAt
                || (o.PlacedAt == cursor.PlacedAt && o.Id.CompareTo(cursor.Id) < 0));
        }

        return await query
            .OrderByDescending(o => o.PlacedAt)
            .ThenByDescending(o => o.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
