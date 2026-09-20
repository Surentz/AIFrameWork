using System.Net;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Monitoring;

/// <summary>
/// The traffic endpoints over real HTTP: the gate and the window validation. What the recorder
/// actually stores, and what the reader sums back out of it, is covered in Infrastructure.Tests
/// against a real database — those types are internal to that layer, and widening the grant just
/// to reach them from here would be the wrong trade.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class TrafficTests(ApiFactory factory)
{
    private readonly ApiFactory _factory = factory;

    [Fact]
    public async Task AMember_IsForbiddenFromTheTrafficEndpoints()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        (await client.GetAsync(new Uri("/api/monitoring/traffic", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(new Uri("/api/monitoring/traffic/series", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnAdministrator_CanReadTheSummaryAndTheSeries()
    {
        var client = await _factory.CreateAdminClientAsync();

        (await client.GetAsync(new Uri("/api/monitoring/traffic", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync(new Uri("/api/monitoring/traffic/series", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnInvalidWindow_IsRefused()
    {
        var client = await _factory.CreateAdminClientAsync();

        var response = await client.GetAsync(
            new Uri("/api/monitoring/traffic?windowMinutes=0", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheSeriesWindow_IsCappedAtADay()
    {
        var client = await _factory.CreateAdminClientAsync();

        // A day of minutes is already 1,440 points. Beyond that the caller is asking for an
        // archive rather than a picture, and the query says so rather than building one.
        var response = await client.GetAsync(
            new Uri("/api/monitoring/traffic/series?windowMinutes=2000", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
