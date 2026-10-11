using System.Text.RegularExpressions;

namespace AiFramework.Application.Assistant;

/// <summary>
/// Masks the personal-data patterns free text can carry before it leaves for the model provider:
/// email addresses and IP addresses. Then truncates.
/// </summary>
/// <remarks>
/// Patterns, not names: "User 'bob' not found" passes through. That residual risk was accepted
/// in ADR 0033 because exception messages are the most useful input for "why did it fail?".
/// IPv6 needs four groups or a "::", so a time such as 00:00:30 is never masked.
/// </remarks>
public static partial class OperationalText
{
    public const int MaxLength = 500;

    private const string Ellipsis = "…";

    public static string? Scrub(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var scrubbed = Email().Replace(text, "[email]");
        scrubbed = IPv4().Replace(scrubbed, "[ip]");
        scrubbed = IPv6Full().Replace(scrubbed, "[ip]");
        scrubbed = IPv6Compressed().Replace(scrubbed, "[ip]");

        return scrubbed.Length <= MaxLength
            ? scrubbed
            : string.Concat(scrubbed.AsSpan(0, MaxLength - Ellipsis.Length), Ellipsis);
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IPv4();

    [GeneratedRegex(@"(?<![\w:])(?:[0-9A-Fa-f]{1,4}:){3,7}[0-9A-Fa-f]{1,4}(?![\w:])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IPv6Full();

    [GeneratedRegex(@"(?<![\w:])(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,5})?::(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,5})?(?![\w:])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IPv6Compressed();
}
