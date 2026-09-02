using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Orders;

[Collection(nameof(ApiFactoryCollection))]
public sealed class OrdersEndpointTests(ApiFactory factory)
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

        // Distinguishes the 400 from PlaceOrderValidator via result.Problem() from
        // [ApiController]'s automatic ModelState validation by title, not by the presence of
        // an "errors" map: after Fix 6 both shapes carry one, so absence no longer
        // distinguishes them. The validator path always produces "validation.failed" (Error's
        // Code); ModelState always produces ASP.NET Core's fixed
        // "One or more validation errors occurred." - see PostOrders_WithMissingQuantity_Returns400
        // for that shape. DataAnnotations were deliberately removed from PlaceOrderRequest so
        // ModelState cannot pre-empt the validation behavior for this request.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("title").GetString().Should().Be("validation.failed");
        root.GetProperty("errors").GetProperty("Quantity").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PostOrders_WithMissingQuantity_Returns400()
    {
        using var client = factory.CreateClient();

        // Quantity is a non-nullable required int with no DataAnnotation; omitting it from the
        // JSON body fails during model binding, before the controller action - and therefore
        // before PlaceOrderValidator - ever runs. This pins what that failure actually returns
        // today: [ApiController]'s automatic ModelState ValidationProblemDetails, not
        // result.Problem().
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("title").GetString().Should().Be("One or more validation errors occurred.");
    }

    [Fact]
    public async Task GetOrders_WithAnUnknownId_Returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Throw_WithADomainException_Returns400WithTheMessageAsDetail()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/test/throw/domain");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("title").GetString().Should().Be("domain.invariant_violated");
        root.GetProperty("detail").GetString().Should().Be("The sku must not be empty.");
    }

    [Fact]
    public async Task Throw_WithAnUnexpectedException_Returns500WithoutLeakingTheMessage()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/test/throw/unexpected");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var body = await response.Content.ReadAsStringAsync();

        // The point of this assertion: it is what stops a future edit to GlobalExceptionHandler
        // from leaking internal exception detail (e.g. a connection string) to the caller.
        body.Should().NotContain("SECRET-CONNECTION-STRING");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        root.GetProperty("title").GetString().Should().Be("internal_error");
        root.GetProperty("detail").GetString().Should().Be("An error occurred.");
    }

    public sealed record OrderResponseDto(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt);
}
