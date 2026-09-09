using System.Net;
using FluentAssertions;

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
}
