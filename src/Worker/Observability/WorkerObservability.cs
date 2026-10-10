using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.Jobs.Scheduling;
using AiFramework.Infrastructure.Observability;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AiFramework.Worker.Observability;

/// <summary>
/// The worker's OpenTelemetry pipeline. Deliberately its own composition rather than a shared one
/// with the Api's <c>ObservabilityRegistration</c>.
/// </summary>
/// <remarks>
/// <para>
/// What the two hosts must agree on — <see cref="ObservabilityOptions"/> and
/// <see cref="OtlpEndpoint"/> — lives in Infrastructure and IS shared. What differs is the
/// instrumentation, and it differs for a real reason: this host's only HTTP surface is two probe
/// endpoints, so ASP.NET instrumentation would emit a span per kubelet probe every ten seconds
/// and nothing else. A single "configurable" registration covering both would be a flag that only
/// ever has one value per host.
/// </para>
/// <para>
/// The tracer provider is registered UNCONDITIONALLY, even with the exporter off, for the reason
/// the Api's own registration gives: it is what makes <c>Activity.Current</c> non-null, which is
/// what lets a job's logs carry the TraceId of the request that enqueued it. Wolverine propagates
/// W3C trace context on its envelopes, and <c>AddSource("Wolverine")</c> below is what collects
/// it — without that line, trace continuity across the job boundary silently does nothing, the
/// same trap <c>AddSource("AiFramework.Outbox")</c> records on the Api side.
/// </para>
/// </remarks>
public static class WorkerObservability
{
    public static WebApplicationBuilder AddWorkerObservability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection("Observability");
        builder.Services.Configure<ObservabilityOptions>(section);

        var options = section.Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        // Distinguishes replicas of the same worker in the log store. Unset outside a container,
        // in which case AddService leaves the SDK to generate one.
        var instanceId = Environment.GetEnvironmentVariable("HOSTNAME");

        // IncludeScopes is load-bearing: it carries Behaviors.LoggedAsync's per-dispatch scope
        // onto every record a handler emits from inside it, which is how a job's own queries are
        // attributable in the log store.
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
                    serviceVersion: ObservabilityResource.ServiceVersionOf(typeof(WorkerObservability).Assembly),
                    serviceInstanceId: string.IsNullOrWhiteSpace(instanceId) ? null : instanceId)
                .AddAttributes(ObservabilityResource.DeploymentAttributes(builder.Environment.EnvironmentName)))
            .WithTracing(tracing => ConfigureTracing(tracing, options))
            .WithMetrics(metrics => ConfigureMetrics(metrics, options));

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

    /// <summary>
    /// Parent-based ratio sampling from <see cref="OtlpOptions.TraceSampleRatio"/> (see its remarks for
    /// what it does and does not affect). Public so it can be tested directly; a ratio outside
    /// (0, 1] stops the host at startup rather than quietly recording everything or nothing.
    /// </summary>
    public static Sampler SamplerFor(OtlpOptions otlp)
    {
        ArgumentNullException.ThrowIfNull(otlp);

        var ratio = otlp.TraceSampleRatio;
        if (double.IsNaN(ratio) || ratio <= 0 || ratio > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(otlp),
                ratio,
                "Observability:Otlp:TraceSampleRatio must be greater than 0 and at most 1. To record no traces, set Observability:Otlp:Traces to false.");
        }

        return new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio));
    }

    /// <summary>Split out of the method above purely to stay under MA0051's line limit.</summary>
    private static void ConfigureTracing(TracerProviderBuilder tracing, ObservabilityOptions options)
    {
        tracing.SetSampler(SamplerFor(options.Otlp));

        // No AddAspNetCoreInstrumentation: see this class's remarks. Outbound HTTP is here
        // because a job legitimately calls out (the mail provider that replaces
        // LoggingOrderNotifier will), and that is exactly what a trace should show.
        tracing.AddHttpClientInstrumentation()
            .AddSource("Wolverine")
            .AddSource("AiFramework.Outbox")
            // A trigger firing and the Wolverine job it enqueues then share one trace.
            .AddSource(QuartzRegistration.ActivitySourceName)
            .AddInfrastructureTracing();

        if (options.Otlp.Enabled && options.Otlp.Traces)
        {
            tracing.AddOtlpExporter(
                exporter => ConfigureExporter(exporter, options.Otlp, OtlpEndpoint.TracesPath));
        }
    }

    /// <summary>
    /// ADR 0027, the worker's half. No ASP.NET Core meter, for the reason this class's remarks
    /// give for tracing: the only HTTP here is two kubelet probes. The runtime and the database
    /// are the point — heavy jobs and pool pressure happen in this host.
    /// </summary>
    private static void ConfigureMetrics(MeterProviderBuilder metrics, ObservabilityOptions options)
    {
        metrics.AddHttpClientInstrumentation()
            .AddMeter("System.Runtime")
            // Wolverine's meter is "Wolverine:<ServiceName>"; see the Api's ConfigureMetrics.
            .AddMeter("Wolverine:*")
            .AddMeter(ExternalSystemMetrics.MeterName)
            .AddInfrastructureMetrics();

        if (options.Otlp.Enabled && options.Otlp.Metrics)
        {
            metrics.AddOtlpExporter(
                exporter => ConfigureExporter(exporter, options.Otlp, OtlpEndpoint.MetricsPath));
        }
    }
}
