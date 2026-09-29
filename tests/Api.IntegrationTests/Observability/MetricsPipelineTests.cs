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

    [Fact]
    public async Task KubeletProbes_AreNotCountedAsRequests()
    {
        // Two API pods probed twice every ten seconds is ~0.4 req/s of 1 ms 200s: enough to dilute
        // the 5xx share, drag p95 down and hold the latency alert's traffic guard permanently open.
        var exported = new List<Metric>();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.ConfigureOpenTelemetryMeterProvider(
                metrics => metrics.AddInMemoryExporter(exported))));
        using var client = host.CreateClient();

        (await client.GetAsync("/health")).EnsureSuccessStatusCode();
        (await client.GetAsync("/health/ready")).EnsureSuccessStatusCode();
        // Anonymous and cheap: an ordinary request, whatever its status.
        using var ordinary = await client.PostAsJsonAsync(
            "/api/auth/register", new { Username = "", Password = "x", DisplayName = "" });
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();

        var routes = RoutesMeasuredBy(exported, "http.server.request.duration");
        routes.Should().Contain("api/auth/register", "an ordinary request is still measured");
        routes.Should().NotContain("/health").And.NotContain("/health/ready");
    }

    private static HashSet<string> RoutesMeasuredBy(IEnumerable<Metric> exported, string metricName)
    {
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in exported.Where(m => string.Equals(m.Name, metricName, StringComparison.Ordinal)))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var tag in point.Tags)
                {
                    if (string.Equals(tag.Key, "http.route", StringComparison.Ordinal) && tag.Value is string route)
                    {
                        routes.Add(route);
                    }
                }
            }
        }

        return routes;
    }
}
