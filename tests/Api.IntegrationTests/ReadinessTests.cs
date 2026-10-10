using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Api.IntegrationTests;

/// <summary>
/// Liveness and readiness are different questions and have different endpoints. /health says
/// the process is alive and touches no database — HealthTests covers it without a container.
/// /health/ready says this pod may receive traffic, which requires Postgres.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class ReadinessTests(ApiFactory factory)
{
    [Fact]
    public async Task GetReady_WhenTheDatabaseIsReachable_Returns200Ok()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetReady_IsAnonymous()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetReady_WithAnUnreachableExternalSystem_StillReturns200()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        // The body is the aggregate status: Healthy only if no external check was run. It also
        // keeps this from being a duplicate of the test above (S4144), which it is not.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [Fact]
    public void Host_RegistersNoHealthCheckPublisher()
    {
        // ADR 0032: only the worker publishes external system status. An API publisher would run
        // every check from every pod and write a table the worker owns.
        var publishers = factory.Services.GetServices<IHealthCheckPublisher>();

        publishers.Should().BeEmpty();
    }

    [Fact]
    public async Task ExternalSystemCheck_ForTheUnreachableSystem_IsRegisteredAndUnhealthy()
    {
        var health = factory.Services.GetRequiredService<HealthCheckService>();

        var report = await health.CheckHealthAsync(c => string.Equals(c.Name, "Unreachable", StringComparison.Ordinal));

        report.Entries["Unreachable"].Status.Should().Be(HealthStatus.Unhealthy,
            "otherwise the 200 above would prove nothing");
    }
}
