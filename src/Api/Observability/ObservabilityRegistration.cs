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
                logging.AddOtlpExporter(
                    exporter => ConfigureExporter(exporter, options.Otlp, OtlpEndpoint.LogsPath));
            }
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: options.ServiceName,
                    serviceVersion: ObservabilityResource.ServiceVersionOf(typeof(ObservabilityRegistration).Assembly),
                    serviceInstanceId: string.IsNullOrWhiteSpace(instanceId) ? null : instanceId)
                .AddAttributes(ObservabilityResource.DeploymentAttributes(builder.Environment.EnvironmentName)))
            .WithTracing(tracing => ConfigureTracing(tracing, options));

        return builder;
    }

    /// <summary>
    /// Every exporter this host creates goes through here, so logs, traces and metrics cannot
    /// disagree about where they go or how they authenticate. Public so it can be tested
    /// directly; OtlpEndpoint.Build's remarks record why the protocol is set explicitly and why
    /// the signal path is appended here rather than left to the SDK.
    /// </summary>
    public static void ConfigureExporter(OtlpExporterOptions exporter, OtlpOptions otlp, string signalPath)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(otlp);

        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
        exporter.Endpoint = OtlpEndpoint.Build(otlp.Endpoint, signalPath);

        // Blank means unset: an unfilled secret arrives as "", which the exporter would try to
        // parse as key=value pairs.
        if (!string.IsNullOrWhiteSpace(otlp.Headers))
        {
            exporter.Headers = otlp.Headers;
        }
    }

    /// <summary>Split out of AddObservability purely to stay under MA0051's line limit.</summary>
    private static void ConfigureTracing(TracerProviderBuilder tracing, ObservabilityOptions options)
    {
        tracing.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            // Wolverine 6 already tags the current span in its generated handlers (see
            // OrderPlacedNotificationHandler1430415712 under Internal/Generated); nothing was
            // collecting those tags until this line.
            .AddSource("Wolverine")
            // OutboxWorkItemProcessor's delivery Activity — without this, StartActivity there
            // always returns null (no listener), and outbox trace continuity silently does
            // nothing.
            .AddSource("AiFramework.Outbox")
            .AddInfrastructureTracing();

        if (options.Otlp.Enabled && options.Otlp.Traces)
        {
            tracing.AddOtlpExporter(
                exporter => ConfigureExporter(exporter, options.Otlp, OtlpEndpoint.TracesPath));
        }
    }
}
