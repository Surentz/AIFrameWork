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

    // No AsNoTracking, deliberately: ShipOrder/CancelOrder mutate what this returns, and an
    // untracked entity would take their write to the floor with no error. See the port.
    public Task<Order?> GetForUpdateAsync(
        Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        context.Orders
            .FirstOrDefaultAsync(o => o.Id == id && o.UserId == ownerId, cancellationToken);

    // Tracked for the same reason as GetForUpdateAsync, and cross-owner by design. See the port.
    public Task<Order?> GetForFulfilmentAsync(Guid id, CancellationToken cancellationToken) =>
        context.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<IReadOnlyList<FulfilmentQueueRow>> ListForFulfilmentAsync(
        OrderStatus status,
        int limit,
        (DateTimeOffset PlacedAt, Guid Id)? after,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // Status is an equality predicate and the leading column of IX_Orders_Status_PlacedAt_Id,
        // so the keyset below is a range scan within one status rather than a sort of the table.
        var query = context.Orders.AsNoTracking().Where(o => o.Status == status);

        if (after is { } cursor)
        {
            // The mirror of ListAsync's keyset, because this list runs OLDEST first: the next page
            // is everything strictly later than the previous page's last row. Translated to
            // Postgres's own uuid comparison, never evaluated in memory - see ListAsync.
            query = query.Where(o => o.PlacedAt > cursor.PlacedAt
                || (o.PlacedAt == cursor.PlacedAt && o.Id.CompareTo(cursor.Id) > 0));
        }

        // LEFT join to users: there is no foreign key, and an order whose buyer row is missing
        // must still reach the queue that ships it. Ordered and limited BEFORE the join, so the
        // join touches one page of rows, never the whole status.
        return await query
            .OrderBy(o => o.PlacedAt)
            .ThenBy(o => o.Id)
            .Take(limit)
            .GroupJoin(
                context.Users.AsNoTracking(),
                o => o.UserId,
                u => u.Id,
                (o, buyers) => new { Order = o, Buyers = buyers })
            .SelectMany(
                x => x.Buyers.DefaultIfEmpty(),
                (x, buyer) => new { x.Order, BuyerUsername = buyer == null ? null : buyer.Username })
            // Re-ordered after the join because a join does not promise to preserve the order of
            // the subquery it wraps. Ordered on the anonymous shape: EF cannot translate an
            // OrderBy over a record it only knows through a constructor call.
            .OrderBy(x => x.Order.PlacedAt)
            .ThenBy(x => x.Order.Id)
            .Select(x => new FulfilmentQueueRow(
                x.Order.Id,
                x.Order.UserId,
                x.BuyerUsername,
                x.Order.Sku,
                x.Order.Quantity,
                x.Order.PlacedAt,
                x.Order.Product == null ? null : x.Order.Product.Name,
                x.Order.Product == null ? null : (decimal?)x.Order.Product.UnitPrice,
                x.Order.Status))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Guid?> GetOwnerAsync(Guid orderId, CancellationToken cancellationToken)
    {
        // Projects to the owner id in SQL rather than loading the order: this is called on the
        // outbox pump's path for every OrderPlaced delivered, and it needs one column.
        // Guid? rather than Guid so "no such order" is distinguishable from Guid.Empty, which
        // Order.Place already refuses to store.
        var owners = await context.Orders.AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => (Guid?)o.UserId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return owners;
    }

    public async Task<IReadOnlyList<Guid>> ListPurchaserIdsAsync(
        string sku, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // GroupBy rather than Select().Distinct(): a user who ordered the same sku five times is
        // one recipient, and grouping lets the "most recent purchase first" ordering be computed
        // per user in SQL. Ordering by Max(PlacedAt) is what makes the cap drop the least recent
        // buyers rather than an arbitrary set - see the port for what that trade buys.
        return await context.Orders.AsNoTracking()
            .Where(o => o.Sku == sku)
            .GroupBy(o => o.UserId)
            .Select(g => new { UserId = g.Key, LastPurchase = g.Max(o => o.PlacedAt) })
            .OrderByDescending(x => x.LastPurchase)
            .Take(limit)
            .Select(x => x.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

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
