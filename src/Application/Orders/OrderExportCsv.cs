using System.Globalization;
using System.Text;
using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

/// <summary>One order as the export writes it: everything the CSV has a column for.</summary>
public sealed record OrderExportRow(
    Guid OrderId,
    string Sku,
    string? ProductName,
    int Quantity,
    decimal? UnitPrice,
    OrderStatus Status,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? CancelledAt,
    string? CancellationReason);

/// <summary>
/// Turns a user's orders into the export's CSV. Pure, so every formatting rule is tested without a
/// database. The byte-order mark Excel needs to read non-ASCII as UTF-8 is not part of this text:
/// it is added when the file is served, so the stored content stays plain.
/// </summary>
/// <remarks>
/// <para>
/// RFC 4180: comma-separated, CRLF after every line including the last, and a field quoted when
/// it contains a comma, a quote, CR or LF, with embedded quotes doubled.
/// </para>
/// <para>
/// <b>Formula injection.</b> Sku, product name and cancellation reason are typed by people. A
/// spreadsheet evaluates a cell that starts with <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> (and
/// some read a leading tab or CR the same way), so "=HYPERLINK(...)" in a product name would run
/// when the owner opens their own export. Such a value gets a leading apostrophe, which a
/// spreadsheet shows as text. Only those three columns: the rest are written by this code from
/// numbers, ids, enums and timestamps, and a negative number must stay a number.
/// </para>
/// </remarks>
public static class OrderExportCsv
{
    private const string Header =
        "OrderId,Sku,Product,Quantity,UnitPrice,Total,Status,PlacedAt,ShippedAt,CancelledAt,CancellationReason";

    private const string LineEnd = "\r\n";

    private static readonly char[] NeedsQuoting = [',', '"', '\r', '\n'];
    private static readonly char[] FormulaStart = ['=', '+', '-', '@', '\t', '\r'];

    public static string Build(IEnumerable<OrderExportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var csv = new StringBuilder(Header).Append(LineEnd);
        foreach (var row in rows)
        {
            string[] fields =
            [
                row.OrderId.ToString(),
                Typed(row.Sku),
                Typed(row.ProductName),
                row.Quantity.ToString(CultureInfo.InvariantCulture),
                Money(row.UnitPrice),
                Money(row.UnitPrice * row.Quantity),
                row.Status.ToString(),
                Timestamp(row.PlacedAt),
                Timestamp(row.ShippedAt),
                Timestamp(row.CancelledAt),
                Typed(row.CancellationReason),
            ];
            csv.AppendJoin(',', fields.Select(Quoted)).Append(LineEnd);
        }

        return csv.ToString();
    }

    private static string Typed(string? value) =>
        value is { Length: > 0 } && FormulaStart.Contains(value[0]) ? "'" + value : value ?? string.Empty;

    private static string Money(decimal? value) =>
        value?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Timestamp(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Quoted(string field) =>
        field.IndexOfAny(NeedsQuoting) >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
}
