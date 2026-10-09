using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Monitoring;

/// <summary>The gate and the shape. What the rows contain is Infrastructure's and Application's to prove.</summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class ExternalSystemsTests(ApiFactory factory)
{
    private static readonly Uri Endpoint = new("/api/monitoring/external-systems", UriKind.Relative);

    [Fact]
    public async Task GetExternalSystems_AsAMember_IsForbidden()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync(Endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetExternalSystems_AsAnAdministrator_ReturnsTheSystemsList()
    {
        var client = await factory.CreateAdminClientAsync();

        var response = await client.GetAsync(Endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("systems").ValueKind.Should().Be(JsonValueKind.Array);
        body.TryGetProperty("trafficSince", out _).Should().BeTrue();
    }
}
