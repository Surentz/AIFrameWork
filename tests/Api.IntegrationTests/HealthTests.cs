using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests;

public sealed class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    // Program.cs now throws at startup when ConnectionStrings:Default is null or whitespace,
    // and appsettings.json ships "" for that key. /health never touches the database, so a
    // placeholder value (never a real Testcontainers instance) is enough to satisfy the
    // startup guard without paying for a container this test doesn't need.
    public HealthTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory.WithWebHostBuilder(
            builder => builder.UseSetting(
                "ConnectionStrings:Default",
                "Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder"));
    }

    [Fact]
    public async Task GetHealth_WhenApplicationIsRunning_Returns200Ok()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
