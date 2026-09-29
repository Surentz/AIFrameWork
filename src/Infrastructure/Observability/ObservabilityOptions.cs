namespace AiFramework.Infrastructure.Observability;

/// <summary>
/// Bound from the "Observability" section inside AddObservability itself, unlike CacheOptions —
/// see src/Infrastructure/CLAUDE.md's note on why CacheOptions is bound in Program.cs instead: a
/// registration that binds configuration itself cannot be resolved from a bare ServiceCollection
/// in a unit test. AddObservability does not have that constraint: it takes the
/// WebApplicationBuilder directly (see its own remarks), so builder.Configuration is always
/// available at the point it runs, and nothing resolves IOptions&lt;ObservabilityOptions&gt; from
/// a bare ServiceCollection anywhere in this codebase's tests.
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>Stamped onto every exported record and span as the OpenTelemetry Resource's
    /// service.name, so two replicas in the same log store are still one logical service.</summary>
    public string ServiceName { get; set; } = "aiframework-api";

    public OtlpOptions Otlp { get; set; } = new();
}

/// <summary>
/// OFF by default. The default must be the one that works with nothing else running: a
/// developer who has not started a collector, and every CI job, must still get a green build
/// and a working (if unexported) tracer provider — Activity.Current is populated either way,
/// which is what makes the existing traceId lines in GlobalExceptionHandler, ResultExtensions
/// and Program.cs resolve to something real instead of falling through to TraceIdentifier.
/// </summary>
public sealed class OtlpOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// The OTLP/HTTP receiver's ROOT — a collector's bare "http://host:4318", or Seq's
    /// "http://host:port/ingest/otlp" — with NO "/v1/logs" or "/v1/traces" suffix.
    /// OtlpEndpoint.Build appends the right one for each signal; do not
    /// put a signal path here, and do not rely on the OTLP SDK to append one itself — it does
    /// not, once Endpoint is set explicitly, which this application always does. See
    /// OtlpEndpoint.Build's remarks for how that was confirmed.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:4318";

    public bool Traces { get; set; } = true;

    /// <summary>
    /// Exports metrics (ADR 0027) when <see cref="Enabled"/> is also true. On by default, like
    /// <see cref="Traces"/>: every OTLP receiver this application is pointed at — Seq 2026.1, the
    /// collector, a hosted backend — takes all three signals. Off is for a receiver that does not.
    /// </summary>
    public bool Metrics { get; set; } = true;

    /// <summary>
    /// The share of ROOT traces this host records, 0 exclusive to 1 inclusive. One by default:
    /// keep everything, which is right for Seq, the in-cluster collector (which tail-samples on
    /// its own, keeping every error and slow trace) and any store that is not billed by volume.
    /// Lower it only when exporting straight to a hosted backend that charges per span.
    /// </summary>
    /// <remarks>
    /// Parent-based: a trace a caller already decided to keep is always kept, so a job continues
    /// the request that enqueued it. It decides what is recorded, never whether a trace id exists
    /// — ProblemDetails, the audit tables and every log record still carry one — and logs are not
    /// sampled at all. 0 is refused rather than meaning "none": that is <see cref="Traces"/> =
    /// false, said plainly.
    /// </remarks>
    public double TraceSampleRatio { get; set; } = 1.0;

    /// <summary>
    /// Headers sent with every export, in OTLP's own <c>key=value,key2=value2</c> form — how a
    /// hosted backend (Grafana Cloud, Honeycomb, Azure Monitor's OTLP ingestion, …) authenticates
    /// the sender. Unset for Seq and for the in-cluster collector, which need none.
    /// </summary>
    /// <remarks>
    /// <b>A secret.</b> It carries an API key, so it comes from the environment or a secret store
    /// as <c>Observability__Otlp__Headers</c> — never from an appsettings file, which is committed.
    /// Blank is treated as unset, so an unfilled secret does not become an unparseable header.
    /// </remarks>
    public string? Headers { get; set; }
}
