using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Orders;

public sealed class OrdersEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task PostOrders_WithAValidRequest_Returns201()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = "SKU-1", Quantity = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task PostOrders_ThenGet_ReturnsTheOrder()
    {
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = "SKU-2", Quantity = 7 });
        var id = await created.Content.ReadFromJsonAsync<Guid>();

        var response = await client.GetFromJsonAsync<OrderResponseDto>($"/api/orders/{id}");

        response.Should().NotBeNull();
        response.Sku.Should().Be("SKU-2");
        response.Quantity.Should().Be(7);
    }

    [Fact]
    public async Task PostOrders_WithZeroQuantity_Returns400()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = "SKU-3", Quantity = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Proves the 400 came from PlaceOrderValidator via result.Problem() (a plain
        // ProblemDetails with title "validation.failed"), not from [ApiController]'s
        // automatic ModelState validation (a ValidationProblemDetails with a per-field
        // "errors" map). DataAnnotations were deliberately removed from PlaceOrderRequest
        // so ModelState cannot pre-empt the validation behavior.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("title").GetString().Should().Be("validation.failed");
        root.TryGetProperty("errors", out _).Should().BeFalse(
            "a ModelState ValidationProblemDetails would carry a per-field errors map");
    }

    [Fact]
    public async Task GetOrders_WithAnUnknownId_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public sealed record OrderResponseDto(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt);
}
