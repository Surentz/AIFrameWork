namespace AiFramework.Api.Observability;

/// <summary>
/// Bound in Program.cs from the "Observability" section, the same way CacheOptions is bound
/// there rather than inside its own registration extension — see
/// src/Infrastructure/CLAUDE.md's note on why: a registration that binds configuration itself
/// cannot be resolved from a bare ServiceCollection in a unit test.
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
    /// ObservabilityRegistration.BuildOtlpEndpoint appends the right one for each signal; do not
    /// put a signal path here, and do not rely on the OTLP SDK to append one itself — it does
    /// not, once Endpoint is set explicitly, which this application always does. See
    /// BuildOtlpEndpoint's remarks for how that was confirmed.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:4318";

    public bool Traces { get; set; } = true;
}
