using System.Globalization;
using System.Text;
using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Orders;

public sealed record OrderListItem(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt);

public sealed record OrderPage(IReadOnlyList<OrderListItem> Items, string? NextCursor);

public sealed record GetOrders(int Limit, string? Cursor) : IQuery<OrderPage>;

/// <summary>
/// Validates its own inputs, because QueryDispatcher does not run the validation behavior —
/// that is command-only. A malformed cursor is a 400, never a 500.
/// </summary>
public sealed class GetOrdersHandler(IOrderRepository orders) : IQueryHandler<GetOrders, OrderPage>
{
    private const int MaxLimit = 100;

    public async Task<Result<OrderPage>> HandleAsync(GetOrders query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit is < 1 or > MaxLimit)
        {
            return Result.Failure<OrderPage>(new Error(
                ErrorKind.Validation, "orders.limit_out_of_range",
                $"Limit must be between 1 and {MaxLimit}."));
        }

        (DateTimeOffset PlacedAt, Guid Id)? after = null;
        if (query.Cursor is not null)
        {
            if (!TryDecode(query.Cursor, out var decoded))
            {
                return Result.Failure<OrderPage>(new Error(
                    ErrorKind.Validation, "orders.malformed_cursor",
                    "The cursor is not a cursor this endpoint issued."));
            }

            after = decoded;
        }

        // One more than asked for, so "is there a next page" needs no second COUNT.
        var rows = await orders
            .ListAsync(query.Limit + 1, after, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit)
            .Select(o => new OrderListItem(o.Id, o.Sku, o.Quantity, o.PlacedAt))
            .ToArray();

        var next = hasMore && page.Length > 0
            ? Encode(page[^1].PlacedAt, page[^1].Id)
            : null;

        return Result.Success(new OrderPage(page, next));
    }

    private static string Encode(DateTimeOffset placedAt, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{placedAt.ToString("O", CultureInfo.InvariantCulture)}|{id}"));

    private static bool TryDecode(string cursor, out (DateTimeOffset PlacedAt, Guid Id) value)
    {
        value = default;

        Span<byte> buffer = new byte[cursor.Length];
        if (!Convert.TryFromBase64String(cursor, buffer, out var written))
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(buffer[..written]).Split('|');
        if (parts.Length != 2
            || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var placedAt)
            || !Guid.TryParse(parts[1], out var id))
        {
            return false;
        }

        value = (placedAt, id);
        return true;
    }
}
