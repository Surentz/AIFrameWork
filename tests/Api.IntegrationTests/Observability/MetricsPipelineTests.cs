using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AiFramework.Api.IntegrationTests.Observability;

/// <summary>
/// ADR 0027: the API measures HTTP, the runtime and the database through OpenTelemetry meters.
/// An in-memory reader stands in for the OTLP exporter, so this proves the meter provider and its
/// meters are wired — with <c>Otlp:Enabled=false</c>, exactly as every test host and CI run it,
/// which is also the proof that measuring does not depend on a collector being there.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class MetricsPipelineTests(ApiFactory factory)
{
    [Fact]
    public async Task ARequest_IsMeasuredByTheHttpRuntimeAndDatabaseMeters()
    {
        var exported = new List<Metric>();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.ConfigureOpenTelemetryMeterProvider(
                metrics => metrics.AddInMemoryExporter(exported))));
        var client = host.CreateClient();

        // Registering touches the database, so the Npgsql meter has something to measure.
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                Username = $"u{Guid.NewGuid():N}"[..32],
                Password = ApiFactory.RegisteredPassword,
                DisplayName = "Metrics Test User",
            });
        response.EnsureSuccessStatusCode();
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();

        var names = exported.Select(metric => metric.Name).ToHashSet(StringComparer.Ordinal);
        names.Should().Contain("http.server.request.duration", "ASP.NET Core's built-in meter");
        names.Should().Contain("db.client.operation.duration", "Npgsql's meter");
        names.Should().Contain(name => name.StartsWith("dotnet.", StringComparison.Ordinal), "the System.Runtime meter");
    }
}
