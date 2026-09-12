using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Orders;

/// <summary>
/// Pins the error body against frontend/src/api/client.ts, which declares its own
/// ProblemDetails interface and reads `detail` and `errors` off it. That interface cannot be
/// generated: ResultExtensions.Problem puts `errors` and `traceId` into
/// ProblemDetails.Extensions rather than typed properties, so OpenAPI never names them and the
/// generated schema cannot describe them.
///
/// That makes this the one part of the contract where backend and frontend can drift in
/// silence, which is exactly why it is asserted here instead of trusted.
///
/// `title` and `errors` are already covered by OrdersEndpointTests.
/// PostOrders_WithZeroQuantity_Returns400; this covers the rest rather than repeating it.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class ProblemDetailsContractTests(ApiFactory factory)
{
    [Fact]
    public async Task AValidationFailure_CarriesTheFieldsTheFrontendReads()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = sku, Quantity = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.TryGetProperty("detail", out var detail).Should().BeTrue(
            "ApiError uses `detail` as its message, falling back to `title` only when absent");
        detail.GetString().Should().NotBeNullOrWhiteSpace();

        root.TryGetProperty("traceId", out var traceId).Should().BeTrue(
            "an operator holding a problem report needs it to find the matching log line");
        traceId.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ANotFound_CarriesTheSameShape()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
        root.TryGetProperty("traceId", out _).Should().BeTrue(
            "every ProblemDetails from ResultExtensions.Problem carries one, not just 400s");
    }
}
