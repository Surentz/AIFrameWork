using System.Globalization;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// How figures read in an answer. Invariant culture throughout: the chiseled runtime image has no
/// ICU, and an answer must read the same on every pod. Times are UTC and say so.
/// </summary>
public static class QuickText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Date(DateTimeOffset at) => at.UtcDateTime.ToString("d MMM yyyy, HH:mm 'UTC'", Invariant);

    public static string Money(decimal amount) => amount.ToString("0.00", Invariant);

    public static string Count(long count) => count.ToString("N0", Invariant);

    public static string Rate(double perMinute) => perMinute.ToString("0.#", Invariant);

    /// <summary>A fraction (0.0123) as a percentage ("1.2%").</summary>
    public static string Percent(double fraction) => fraction.ToString("0.0%", Invariant);

    public static string Milliseconds(double? ms) => ms is { } value ? $"{value.ToString("0", Invariant)} ms" : "no data";

    /// <summary>The first eight hex digits of an id, as the order list shows them.</summary>
    public static string ShortId(Guid id) => id.ToString("N", Invariant)[..8];

    public static string Window(TimeSpan window) => window.TotalHours switch
    {
        1d => "the last hour",
        24d => "the last 24 hours",
        168d => "the last 7 days",
        var hours when hours % 24 == 0 => $"the last {((int)hours / 24).ToString(Invariant)} days",
        var hours => $"the last {((int)hours).ToString(Invariant)} hours",
    };
}
