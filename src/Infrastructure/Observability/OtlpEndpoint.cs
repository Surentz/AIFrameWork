namespace AiFramework.Infrastructure.Observability;

/// <summary>
/// Builds the full OTLP/HTTP endpoint for one signal from the configured receiver ROOT.
/// </summary>
/// <remarks>
/// Lives in Infrastructure rather than beside the Api's <c>ObservabilityRegistration</c> because
/// BOTH hosts export: the API and the worker each compose their own OpenTelemetry pipeline (they
/// differ — the worker runs no ASP.NET instrumentation, having no meaningful HTTP surface) but
/// they must agree exactly on this. It is pure URI arithmetic with no host, no exporter and no
/// ASP.NET dependency, so moving it here costs Infrastructure nothing. See ADR 0016.
/// </remarks>
public static class OtlpEndpoint
{
    /// <summary>The logs signal's path, appended to the receiver root.</summary>
    public const string LogsPath = "v1/logs";

    /// <summary>The traces signal's path, appended to the receiver root.</summary>
    public const string TracesPath = "v1/traces";

    /// <summary>The metrics signal's path, appended to the receiver root.</summary>
    public const string MetricsPath = "v1/metrics";

    /// <summary>
    /// Public and pure — no host, no exporter, no network — specifically so this can be unit
    /// tested directly.
    /// </summary>
    /// <remarks>
    /// Both of the two things this method exists to get right were confirmed empirically against
    /// a real Seq 2026.1 container while implementing centralized logging
    /// (docs/superpowers/plans/2026-09-13-centralized-logging.md, Task 6), not assumed from
    /// documentation — because getting either wrong fails SILENTLY. The exporter's own
    /// EventSource records the failure, but nothing surfaces it to the application or to a log
    /// record, so the visible symptom is "Otlp:Enabled is true" and an empty log store, with no
    /// exception and no log line anywhere pointing at why:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>OtlpExporterOptions.Protocol</c> defaults to gRPC (HTTP/2) whenever it is left unset —
    /// confirmed by pointing an unconfigured exporter at a plain HTTP/1.1 listener and capturing
    /// "PRI * HTTP/2.0" instead of a POST. Both Seq and a collector's HTTP receiver need
    /// <c>HttpProtobuf</c> set explicitly; this method's caller does that, not this method.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// Once <c>Endpoint</c> is assigned explicitly — which every caller here always does — the
    /// SDK appends NOTHING to it. Confirmed by capturing the raw outbound request: an exporter
    /// pointed at "http://host/ingest/otlp" posts to exactly that path, never
    /// "http://host/ingest/otlp/v1/logs" — even though the OTLP/HTTP spec, and Seq's own
    /// receiver, require the signal-specific suffix. Appending it is this method's whole job.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    public static Uri Build(string receiverRoot, string signalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalPath);

        return new Uri($"{receiverRoot.TrimEnd('/')}/{signalPath.TrimStart('/')}");
    }
}
