using System.Globalization;
using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

/// <summary>
/// Everything the export's PDF says, already worked out and formatted: the renderer only places
/// these strings. Pure, so every rule here is tested without drawing a page. ADR 0030.
/// </summary>
/// <remarks>
/// Times are UTC, said once in <see cref="Generated"/>: the worker does not know the reader's time
/// zone. Amounts have two decimals and no currency symbol, because the domain has no currency.
/// </remarks>
public sealed record OrderExportReport(
    string OwnerName,
    string Generated,
    int OrderCount,
    string? Period,
    string TotalValue,
    string TotalValueNote,
    string StatusSummary,
    IReadOnlyList<OrderExportReportRow> Rows)
{
    public bool IsEmpty => Rows.Count == 0;

    public static OrderExportReport Create(
        IReadOnlyCollection<OrderExportRow> rows, string ownerName, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(ownerName);

        // Counted: cancelled orders are not value, and an order with no recorded price has none to add.
        var counted = rows.Where(r => r.Status != OrderStatus.Cancelled).ToArray();
        var total = counted.Where(r => r.UnitPrice is not null).Sum(r => r.UnitPrice!.Value * r.Quantity);
        var unpriced = counted.Count(r => r.UnitPrice is null);

        return new OrderExportReport(
            ownerName,
            $"Generated {generatedAt.UtcDateTime.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)} UTC",
            rows.Count,
            PeriodOf(rows),
            Money(total),
            unpriced == 0
                ? "excludes cancelled orders"
                : $"excludes cancelled orders and {unpriced.ToString(CultureInfo.InvariantCulture)} without a recorded price",
            $"Placed {Count(rows, OrderStatus.Placed)} · Shipped {Count(rows, OrderStatus.Shipped)} · Cancelled {Count(rows, OrderStatus.Cancelled)}",
            [.. rows.OrderByDescending(r => r.PlacedAt).ThenByDescending(r => r.OrderId).Select(ToRow)]);
    }

    private static string? PeriodOf(IReadOnlyCollection<OrderExportRow> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var first = Date(rows.Min(r => r.PlacedAt));
        var last = Date(rows.Max(r => r.PlacedAt));
        return string.Equals(first, last, StringComparison.Ordinal) ? first : $"{first} – {last}";
    }

    private static string Count(IReadOnlyCollection<OrderExportRow> rows, OrderStatus status) =>
        rows.Count(r => r.Status == status).ToString(CultureInfo.InvariantCulture);

    private static OrderExportReportRow ToRow(OrderExportRow row) => new(
        Date(row.PlacedAt),
        row.OrderId.ToString("N")[..8].ToUpperInvariant(),
        row.ProductName ?? row.Sku,
        row.ProductName is null ? null : row.Sku,
        row.Quantity.ToString(CultureInfo.InvariantCulture),
        row.UnitPrice is { } price ? Money(price) : "–",
        row.UnitPrice is { } unit ? Money(unit * row.Quantity) : "–",
        row.Status,
        row.Status switch
        {
            OrderStatus.Shipped when row.ShippedAt is { } shipped => $"Shipped {Date(shipped)}",
            OrderStatus.Cancelled when row.CancelledAt is { } cancelled => $"Cancelled {Date(cancelled)}",
            _ => null,
        },
        row.Status == OrderStatus.Cancelled ? row.CancellationReason : null);

    private static string Date(DateTimeOffset value) =>
        value.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);
}

/// <summary>One order as the PDF's table shows it. <see cref="Sku"/> is null when it is already the product.</summary>
public sealed record OrderExportReportRow(
    string Placed,
    string Reference,
    string Product,
    string? Sku,
    string Quantity,
    string UnitPrice,
    string Total,
    OrderStatus Status,
    string? StatusDetail,
    string? CancellationReason);
