namespace AiFramework.Api.Monitoring;

/// <summary>
/// What the monitoring page needs from configuration. Bound from the "Monitoring" section in
/// Program.cs — <c>Monitoring__TraceLinkTemplate</c>, double underscores.
/// </summary>
/// <remarks>
/// Api-owned, not a member of Infrastructure's <c>ObservabilityOptions</c> where the log store's
/// other settings live: a controller may reach Infrastructure only through DI
/// (src/Api/CLAUDE.md), and this is read by <see cref="MonitoringController"/>. It shares the
/// "Monitoring" section with Infrastructure's retention settings; binding ignores the keys a class
/// does not declare, so the two never see each other's values.
/// </remarks>
public sealed class MonitoringPageOptions
{
    /// <summary>Replaced by the SPA with the row's 32-hex W3C trace id.</summary>
    public const string Placeholder = "{traceId}";

    /// <summary>
    /// A URL into the log store that shows one trace, with <see cref="Placeholder"/> where the id
    /// goes — for Seq, <c>http://localhost:55341/#/events?filter=@TraceId%20%3D%20'{traceId}'</c>.
    /// Unset, the page shows trace ids as plain text, which is the right degradation where no log
    /// store is running (ADR 0021).
    /// </summary>
    public string? TraceLinkTemplate { get; set; }

    /// <summary>
    /// <see cref="TraceLinkTemplate"/> as the SPA should see it: null for blank as well as unset,
    /// because an empty environment variable or an unfilled secret arrives as "" rather than as
    /// absent. Read-only, so the configuration binder leaves it alone.
    /// </summary>
    public string? LinkTemplate =>
        string.IsNullOrWhiteSpace(TraceLinkTemplate) ? null : TraceLinkTemplate;

    /// <summary>
    /// True for an unset template, or one that becomes an absolute http(s) URL once the
    /// placeholder is filled. Everything else is refused at startup rather than shipped to the
    /// browser as an <c>href</c> — a <c>javascript:</c> template would run in an administrator's
    /// session, and a template with no placeholder links every row to the same page.
    /// </summary>
    public static bool IsValidTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return true;
        }

        if (!template.Contains(Placeholder, StringComparison.Ordinal))
        {
            return false;
        }

        var sample = template.Replace(
            Placeholder, "0123456789abcdef0123456789abcdef", StringComparison.Ordinal);

        return Uri.TryCreate(sample, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }
}
