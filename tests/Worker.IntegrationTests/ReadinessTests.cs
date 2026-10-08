using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Worker.IntegrationTests;

[Collection(nameof(WorkerFactoryCollection))]
public sealed class ReadinessTests(WorkerFactory factory)
{
    [Fact]
    public async Task GetReady_WithAnUnreachableExternalSystem_StillReturns200()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExternalSystemCheck_ForTheUnreachableSystem_IsRegisteredAndUnhealthy()
    {
        var health = factory.Services.GetRequiredService<HealthCheckService>();

        var report = await health.CheckHealthAsync(c => string.Equals(c.Name, "Unreachable", StringComparison.Ordinal));

        report.Entries["Unreachable"].Status.Should().Be(HealthStatus.Unhealthy);
    }
}
