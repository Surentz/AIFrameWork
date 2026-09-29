using AiFramework.Application.Abstractions;
using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

public sealed record FulfilmentQueueItem(
    Guid Id,
    Guid BuyerId,
    string? BuyerUsername,
    string Sku,
    int Quantity,
    DateTimeOffset PlacedAt,
    string? ProductName,
    decimal? UnitPrice,
    OrderStatus Status);

public sealed record FulfilmentQueuePage(IReadOnlyList<FulfilmentQueueItem> Items, string? NextCursor);

/// <summary>
/// Every buyer's orders in one status, oldest first — what an operator works through to ship.
/// </summary>
/// <remarks>
/// NOT <c>ICacheable</c>, deliberately: an operations view has to be current, and a thirty-second
/// stale queue shows an order another operator has just shipped as still waiting. Authorization is
/// the <c>Orders.Fulfil</c> policy on the controller; the repository read is the explicitly
/// cross-owner one. See ADR 0024.
/// </remarks>
public sealed record GetOrdersToFulfil(OrderStatus Status, int Limit, string? Cursor)
    : IQuery<FulfilmentQueuePage>;

/// <summary>
/// Validates its own inputs, because QueryDispatcher does not run the validation behavior —
/// that is command-only. A malformed cursor is a 400, never a 500.
/// </summary>
public sealed class GetOrdersToFulfilHandler(IOrderRepository orders)
    : IQueryHandler<GetOrdersToFulfil, FulfilmentQueuePage>
{
    private const int MaxLimit = 100;

    public async Task<Result<FulfilmentQueuePage>> HandleAsync(
        GetOrdersToFulfil query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit is < 1 or > MaxLimit)
        {
            return Result.Failure<FulfilmentQueuePage>(new Error(
                ErrorKind.Validation, "orders.limit_out_of_range",
                $"Limit must be between 1 and {MaxLimit}."));
        }

        if (!Enum.IsDefined(query.Status))
        {
            return Result.Failure<FulfilmentQueuePage>(new Error(
                ErrorKind.Validation, "orders.unknown_status",
                $"'{query.Status}' is not an order status."));
        }

        (DateTimeOffset PlacedAt, Guid Id)? after = null;
        if (query.Cursor is not null)
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var decoded))
            {
                return Result.Failure<FulfilmentQueuePage>(new Error(
                    ErrorKind.Validation, "orders.malformed_cursor",
                    "The cursor could not be parsed."));
            }

            after = decoded;
        }

        // One more than asked for, so "is there a next page" needs no second COUNT.
        var rows = await orders
            .ListForFulfilmentAsync(query.Status, query.Limit + 1, after, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit)
            .Select(r => new FulfilmentQueueItem(
                r.Id, r.BuyerId, r.BuyerUsername, r.Sku, r.Quantity, r.PlacedAt,
                r.ProductName, r.UnitPrice, r.Status))
            .ToArray();

        var next = hasMore
            ? KeysetCursor.Encode(page[^1].PlacedAt, page[^1].Id)
            : null;

        return Result.Success(new FulfilmentQueuePage(page, next));
    }
}
