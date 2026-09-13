using AiFramework.Infrastructure.Observability;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AiFramework.Api.Observability;

public static class ObservabilityRegistration
{
    /// <summary>
    /// Wires ILogger export and distributed tracing for the whole application. Takes the
    /// WebApplicationBuilder directly, not IServiceCollection, because it must reach both
    /// builder.Logging (the ILoggerFactory pipeline) and builder.Services (the tracer
    /// provider) — the two are configured through different builder surfaces, unlike
    /// AddInfrastructure and AddCaching, which only ever touch IServiceCollection.
    /// </summary>
    /// <remarks>
    /// The tracer provider is registered UNCONDITIONALLY, even when the OTLP exporter itself is
    /// off. That is deliberate: it is what makes Activity.Current non-null on every request,
    /// which is what makes the existing traceId lines in GlobalExceptionHandler,
    /// ResultExtensions and Program.cs resolve to something a log record can actually be found
    /// by, instead of silently falling through to HttpContext.TraceIdentifier — a value that
    /// appears in no log record anywhere. Export and instrumentation are separate switches; only
    /// export is configuration-gated, and it defaults OFF so a developer with no collector
    /// running, and every CI job, still gets a green build.
    /// </remarks>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection("Observability");
        builder.Services.Configure<ObservabilityOptions>(section);

        var options = section.Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        // Distinguishes replicas of the same service in the log store. Unset outside a
        // container, in which case AddService leaves the SDK to generate one, same as if this
        // were never passed.
        var instanceId = Environment.GetEnvironmentVariable("HOSTNAME");

        // IncludeScopes is load-bearing, not decoration: it is what carries the logging
        // behavior's per-dispatch scope (command/query name, current user id — see
        // Infrastructure/Messaging/Behaviors.LoggedAsync) onto every record a handler emits from
        // inside it.
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeScopes = true;
            logging.IncludeFormattedMessage = true;

            if (options.Otlp.Enabled)
            {
                logging.AddOtlpExporter(exporter =>
                {
                    exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                    exporter.Endpoint = BuildOtlpEndpoint(options.Otlp.Endpoint, "v1/logs");
                });
            }
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: options.ServiceName,
                serviceInstanceId: string.IsNullOrWhiteSpace(instanceId) ? null : instanceId))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    // Wolverine 6 already tags the current span in its generated handlers (see
                    // OrderPlacedNotificationHandler1430415712 under Internal/Generated); nothing
                    // was collecting those tags until this line.
                    .AddSource("Wolverine")
                    .AddInfrastructureTracing();

                if (options.Otlp.Enabled && options.Otlp.Traces)
                {
                    tracing.AddOtlpExporter(exporter =>
                    {
                        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                        exporter.Endpoint = BuildOtlpEndpoint(options.Otlp.Endpoint, "v1/traces");
                    });
                }
            });

        return builder;
    }

    /// <summary>
    /// Builds the full OTLP/HTTP endpoint for one signal from the configured receiver ROOT.
    /// Public and pure — no host, no exporter, no network — specifically so this can be unit
    /// tested directly, the same way <see cref="AiFramework.Api.ResultExtensions"/> stays a
    /// plain public static class rather than something narrower that would need an
    /// <c>InternalsVisibleTo</c> this project has never needed before.
    /// </summary>
    /// <remarks>
    /// Both of the two things this method exists to get right were confirmed empirically against
    /// a real Seq 2026.1 container while implementing this
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
    public static Uri BuildOtlpEndpoint(string receiverRoot, string signalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalPath);

        return new Uri($"{receiverRoot.TrimEnd('/')}/{signalPath.TrimStart('/')}");
    }
}
