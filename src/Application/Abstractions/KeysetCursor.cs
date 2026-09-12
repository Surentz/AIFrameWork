using System.Globalization;
using System.Text;

namespace AiFramework.Application.Abstractions;

/// <summary>
/// The opaque pagination cursor every keyset-paged query in this layer speaks: a timestamp and
/// a tie-breaking id, round-tripped through base64. Extracted from <c>GetOrdersHandler</c> when
/// <c>GetProductsHandler</c> needed the identical format — one definition, because the encoded
/// string is a public API contract and a cache key fragment, so two copies drifting apart would
/// hand clients cursors the other side cannot read.
/// </summary>
/// <remarks>
/// Not a security boundary. Base64 is here to keep an ordering key out of the client's way, not
/// to hide it — a caller can decode one trivially, and a caller that forges one reaches only
/// rows it was already allowed to read, because the owner filter is applied separately, in SQL.
/// What every caller must do is treat a cursor that fails to decode as a validation failure
/// (400), never as an empty first page.
/// </remarks>
public static class KeysetCursor
{
    /// <summary>Round-trip ("O") so the timestamp survives re-parsing to the tick.</summary>
    public static string Encode(DateTimeOffset timestamp, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{timestamp.ToString("O", CultureInfo.InvariantCulture)}|{id}"));

    public static bool TryDecode(string cursor, out (DateTimeOffset Timestamp, Guid Id) value)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        value = default;

        // Decoded bytes are never more numerous than base64 characters, so the input's own
        // length is a safe upper bound for the buffer.
        Span<byte> buffer = new byte[cursor.Length];
        if (!Convert.TryFromBase64String(cursor, buffer, out var written))
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(buffer[..written]).Split('|');
        if (parts.Length != 2
            || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var timestamp)
            || !Guid.TryParse(parts[1], out var id))
        {
            return false;
        }

        value = (timestamp, id);
        return true;
    }
}
