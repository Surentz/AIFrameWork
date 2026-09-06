using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class OrderRepository(AiFrameworkDbContext context) : IOrderRepository
{
    public async Task AddAsync(Order order, CancellationToken cancellationToken) =>
        await context.Orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

    public Task<Order?> GetAsync(Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        context.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && o.UserId == ownerId, cancellationToken);

    public async Task<IReadOnlyList<Order>> ListAsync(
        Guid ownerId,
        int limit,
        (DateTimeOffset PlacedAt, Guid Id)? after,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // The owner filter goes first, and stays in SQL: it is the leading column of
        // IX_Orders_UserId_PlacedAt_Id_Desc, and filtering a fetched page in memory would
        // break the keyset - short pages, wrong cursors, skipped rows.
        var query = context.Orders.AsNoTracking().Where(o => o.UserId == ownerId);

        if (after is { } cursor)
        {
            // Keyset, not offset: a row inserted while a caller sits on page 1 must not make
            // page 2 repeat what page 1 already showed. Both the comparison and the ordering run
            // as Postgres SQL, never as CLR code: EF Core translates o.Id.CompareTo(...) into
            // Postgres's own uuid comparison operator, and throws rather than silently
            // client-evaluating a clause it cannot translate. Never re-sort a page in memory -
            // that is the one thing that would put .NET's own Guid ordering in the path instead.
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
