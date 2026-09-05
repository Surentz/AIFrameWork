using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests;

/// <summary>
/// The document is a Development-only convenience, and that is a security property rather than
/// a preference: a deployed instance must not publish its endpoint surface. Both halves are
/// asserted, because only asserting the happy path would let a missing environment check ship.
///
/// Container-free on purpose, like HealthTests: neither endpoint touches the database, so the
/// placeholder connection string plus Wolverine:Durable=false is enough to boot the host.
/// </summary>
public sealed class OpenApiDocumentTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string PlaceholderConnectionString =
        "Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder";

    private WebApplicationFactory<Program> ForEnvironment(string environmentName)
        => factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", PlaceholderConnectionString);
            builder.UseSetting("Wolverine:Durable", "false");
            builder.UseEnvironment(environmentName);
        });

    [Fact]
    public async Task GetOpenApiDocument_InDevelopment_DescribesTheOrdersEndpoints()
    {
        using var client = ForEnvironment("Development").CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("paths")
            .TryGetProperty("/api/orders", out _).Should().BeTrue(
                "the document is worthless if it does not describe the one controller there is");
    }

    [Fact]
    public async Task GetOpenApiDocument_OutsideDevelopment_IsNotServed()
    {
        using var client = ForEnvironment("Production").CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a deployed instance must not publish its endpoint surface");
    }

    [Fact]
    public async Task GetScalarUi_OutsideDevelopment_IsNotServed()
    {
        using var client = ForEnvironment("Production").CreateClient();

        var response = await client.GetAsync("/scalar/v1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
