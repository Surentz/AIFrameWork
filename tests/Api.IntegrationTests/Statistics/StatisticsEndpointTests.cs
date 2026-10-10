using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Statistics;

/// <summary>
/// The pilot's HTTP surface. ApiFactory configures no StatisticsDenmark system, so the source
/// fails fast as not configured: every test that reaches it observes a deterministic 503 with no
/// network call. The mapping of StatBank's answers is StatBankAdapterTests' job.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class StatisticsEndpointTests(ApiFactory factory)
{
    [Fact]
    public async Task GetPopulation_WhenAnonymous_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/statistics/population?area=101");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPopulation_WithAMalformedArea_Returns400WithoutCallingTheSource()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/statistics/population?area=1a1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString().Should().Be("statistics.invalid_area");
    }

    [Fact]
    public async Task GetPopulation_WithoutAnArea_Returns400FromTheHandler()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/statistics/population");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString().Should().Be("statistics.invalid_area");
    }

    [Fact]
    public async Task GetPopulation_WhenTheSourceIsUnavailable_Returns503WithRetryAfter()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/statistics/population?area=101");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task GetPopulationAreas_WhenTheSourceIsUnavailable_Returns503()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/statistics/population/areas");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }
}
