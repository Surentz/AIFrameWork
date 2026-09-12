using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Orders;

public sealed record OrderListItem(
    Guid Id,
    string Sku,
    int Quantity,
    DateTimeOffset PlacedAt,
    Guid? ProductId,
    string? ProductName,
    decimal? UnitPrice);

public sealed record OrderPage(IReadOnlyList<OrderListItem> Items, string? NextCursor);

public sealed record GetOrders(int Limit, string? Cursor) : IQuery<OrderPage>, ICacheable
{
    // The user scope is NOT here — the caching behavior prepends the query type and the caller's
    // id. Putting a user id in this string would duplicate it, not secure it.
    //
    // A prefix discriminator, not a substituted sentinel: null and "" are not the same request
    // — the handler below branches on `Cursor is not null`, so null takes the first-page path
    // and "" takes the malformed-cursor 400 path — and they must not share a cache key, or a
    // cached response for one gets served to the other. An earlier version of this coalesced
    // null to a fixed literal ("-"), but that only moves the collision: a CLIENT can send
    // `?cursor=-` (ordinary, non-empty, so MVC's ConvertEmptyStringToNull leaves it alone), and
    // that request must decode-and-fail like any other malformed cursor, not share a key with
    // Cursor == null. What actually makes this safe is that "N" is the null case's WHOLE key
    // fragment, and every non-null case is prefixed "C" before the raw cursor — no client-
    // supplied cursor, of any content, can produce the literal string "N", because doing so
    // would require the fragment to start with "C". Limit is an int and contains no colon, so
    // that first colon still delimits the fragment unambiguously regardless of what a client
    // puts in Cursor.
    public string CacheKey => $"{Limit}:{(Cursor is null ? "N" : $"C{Cursor}")}";

    // Thirty seconds: long enough that a refocus-driven refetch is a hit, short enough that a
    // write from another device shows up without anyone waiting on it. Clamped by
    // CacheOptions.MaximumDuration.
    public TimeSpan Duration => TimeSpan.FromSeconds(30);
}

/// <summary>
/// Validates its own inputs, because QueryDispatcher does not run the validation behavior —
/// that is command-only. A malformed cursor is a 400, never a 500.
/// </summary>
public sealed class GetOrdersHandler(IOrderRepository orders, ICurrentUser currentUser)
    : IQueryHandler<GetOrders, OrderPage>
{
    private const int MaxLimit = 100;

    public async Task<Result<OrderPage>> HandleAsync(GetOrders query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<OrderPage>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        if (query.Limit is < 1 or > MaxLimit)
        {
            return Result.Failure<OrderPage>(new Error(
                ErrorKind.Validation, "orders.limit_out_of_range",
                $"Limit must be between 1 and {MaxLimit}."));
        }

        (DateTimeOffset PlacedAt, Guid Id)? after = null;
        if (query.Cursor is not null)
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var decoded))
            {
                return Result.Failure<OrderPage>(new Error(
                    ErrorKind.Validation, "orders.malformed_cursor",
                    "The cursor could not be parsed."));
            }

            after = decoded;
        }

        // One more than asked for, so "is there a next page" needs no second COUNT.
        var rows = await orders
            .ListAsync(userId, query.Limit + 1, after, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit)
            .Select(o => new OrderListItem(
                o.Id, o.Sku, o.Quantity, o.PlacedAt,
                o.Product?.ProductId, o.Product?.Name, o.Product?.UnitPrice))
            .ToArray();

        var next = hasMore
            ? KeysetCursor.Encode(page[^1].PlacedAt, page[^1].Id)
            : null;

        return Result.Success(new OrderPage(page, next));
    }
}
