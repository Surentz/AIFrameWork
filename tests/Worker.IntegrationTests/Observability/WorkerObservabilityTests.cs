using AiFramework.Infrastructure.Observability;
using AiFramework.Worker.Observability;
using FluentAssertions;
using OpenTelemetry.Exporter;

namespace AiFramework.Worker.IntegrationTests.Observability;

/// <summary>
/// The worker composes its own exporters (ADR 0016), so it has its own copy of the one piece every
/// exporter must get right. These pin that it agrees with the API's.
/// </summary>
public sealed class WorkerObservabilityTests
{
    [Fact]
    public void ConfigureExporter_SpeaksHttpProtobufToTheSignalsOwnPath()
    {
        var exporter = new OtlpExporterOptions();

        WorkerObservability.ConfigureExporter(
            exporter, new OtlpOptions { Endpoint = "http://collector:4318" }, OtlpEndpoint.LogsPath);

        exporter.Protocol.Should().Be(OtlpExportProtocol.HttpProtobuf);
        exporter.Endpoint.Should().Be(new Uri("http://collector:4318/v1/logs"));
    }

    [Fact]
    public void ConfigureExporter_WithHeaders_SendsThem()
    {
        var exporter = new OtlpExporterOptions();

        WorkerObservability.ConfigureExporter(
            exporter, new OtlpOptions { Headers = "Authorization=Basic abc123" }, OtlpEndpoint.TracesPath);

        exporter.Headers.Should().Be("Authorization=Basic abc123");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfigureExporter_WithBlankHeaders_SendsNone(string? headers)
    {
        var exporter = new OtlpExporterOptions();

        WorkerObservability.ConfigureExporter(
            exporter, new OtlpOptions { Headers = headers }, OtlpEndpoint.TracesPath);

        exporter.Headers.Should().BeNull();
    }
}
