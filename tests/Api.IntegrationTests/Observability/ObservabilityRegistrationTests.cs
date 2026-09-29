using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiFramework.Api.Observability;
using AiFramework.Infrastructure.Observability;
using FluentAssertions;
using OpenTelemetry.Exporter;

namespace AiFramework.Api.IntegrationTests.Observability;

/// <summary>
/// Proves the payoff, not the plumbing: before this plan, every traceId in a ProblemDetails
/// body fell through to HttpContext.TraceIdentifier because Activity.Current was always null
/// with no tracer provider registered — see
/// docs/superpowers/plans/2026-09-13-centralized-logging.md's "What exists today" section. This
/// asserts the value is now a real W3C trace id, which is what makes it findable in a log store.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed partial class ObservabilityRegistrationTests(ApiFactory factory)
{
    // 00-<32 hex trace id>-<16 hex span id>-<2 hex flags>, per the W3C Trace Context spec that
    // System.Diagnostics.Activity.Id follows in its default (W3C) format.
    [GeneratedRegex(
        "^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex W3CTraceParent();

    [Fact]
    public async Task AnUnhandledException_CarriesAW3CFormattedTraceId()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/test/throw/unexpected");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var traceId = document.RootElement.GetProperty("traceId").GetString();

        // Otlp:Enabled is false for every test host (ApiFactory.ConfigureWebHost), so this also
        // proves the tracer provider is registered — and therefore Activity.Current is non-null
        // during a request — independently of whether anything is exporting it.
        traceId.Should().MatchRegex(
            W3CTraceParent(),
            "with a tracer provider registered, Activity.Current.Id replaces the bare " +
            "TraceIdentifier every one of GlobalExceptionHandler, ResultExtensions and " +
            "Program.cs's OnRejected falls back to when none is");
    }

    /// <summary>
    /// OtlpEndpoint.Build's two jobs, confirmed empirically against a real Seq container per its
    /// own remarks: append the signal path (never rely on the SDK to), and do it without
    /// dropping or duplicating a slash regardless of how the configured root is spelled.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:4318", "v1/logs", "http://localhost:4318/v1/logs")]
    [InlineData("http://localhost:4318/", "v1/logs", "http://localhost:4318/v1/logs")]
    [InlineData(
        "http://localhost:55341/ingest/otlp", "v1/traces", "http://localhost:55341/ingest/otlp/v1/traces")]
    [InlineData(
        "http://otel-collector.aiframework:4318/", "v1/traces",
        "http://otel-collector.aiframework:4318/v1/traces")]
    public void BuildOtlpEndpoint_AppendsExactlyOneSlashBetweenRootAndSignalPath(
        string receiverRoot, string signalPath, string expected)
    {
        var endpoint = OtlpEndpoint.Build(receiverRoot, signalPath);

        endpoint.Should().Be(new Uri(expected));
    }

    [Fact]
    public void ConfigureExporter_SpeaksHttpProtobufToTheSignalsOwnPath()
    {
        var exporter = new OtlpExporterOptions();

        ObservabilityRegistration.ConfigureExporter(
            exporter, new OtlpOptions { Endpoint = "http://collector:4318" }, OtlpEndpoint.TracesPath);

        exporter.Protocol.Should().Be(OtlpExportProtocol.HttpProtobuf);
        exporter.Endpoint.Should().Be(new Uri("http://collector:4318/v1/traces"));
    }

    [Fact]
    public void ConfigureExporter_WithHeaders_SendsThem()
    {
        // What a hosted backend (Grafana Cloud, Honeycomb, …) authenticates the export with.
        var exporter = new OtlpExporterOptions();

        ObservabilityRegistration.ConfigureExporter(
            exporter,
            new OtlpOptions { Headers = "Authorization=Basic abc123,X-Scope-OrgID=tenant-1" },
            OtlpEndpoint.LogsPath);

        exporter.Headers.Should().Be("Authorization=Basic abc123,X-Scope-OrgID=tenant-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfigureExporter_WithBlankHeaders_SendsNone(string? headers)
    {
        // An unfilled secret arrives as "" - and the exporter would try to parse it as k=v pairs.
        var exporter = new OtlpExporterOptions();

        ObservabilityRegistration.ConfigureExporter(
            exporter, new OtlpOptions { Headers = headers }, OtlpEndpoint.LogsPath);

        exporter.Headers.Should().BeNull();
    }
}
