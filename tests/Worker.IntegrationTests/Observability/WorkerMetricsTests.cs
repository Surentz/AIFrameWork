using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AiFramework.Worker.IntegrationTests.Observability;

/// <summary>
/// ADR 0027, the worker's half: no ASP.NET Core meter (its HTTP surface is two probes), but the
/// runtime and the database — the job host is where heavy work and pool pressure actually happen.
/// </summary>
[Collection(nameof(WorkerFactoryCollection))]
public sealed class WorkerMetricsTests(WorkerFactory factory)
{
    [Fact]
    public void TheWorker_IsMeasuredByTheRuntimeAndDatabaseMeters()
    {
        var exported = new List<Metric>();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.ConfigureOpenTelemetryMeterProvider(
                metrics => metrics.AddInMemoryExporter(exported))));

        // Starting the host is enough: durable Wolverine opens pooled connections as it starts.
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();

        var names = exported.Select(metric => metric.Name).ToHashSet(StringComparer.Ordinal);
        names.Should().Contain("db.client.connection.count", "Npgsql's meter");
        names.Should().Contain(name => name.StartsWith("dotnet.", StringComparison.Ordinal), "the System.Runtime meter");
    }
}
