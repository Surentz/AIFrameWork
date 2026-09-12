using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Products;

[Collection(nameof(ApiFactoryCollection))]
public sealed class ProductsEndpointTests(ApiFactory factory)
{
    /// <summary>
    /// Unique per call. The catalogue is global and these tests share one database, so a fixed
    /// sku would collide with another test's row on the unique index.
    /// </summary>
    private static string NewSku() => $"SKU-{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    private static object NewProduct(string sku, string name = "Widget", decimal price = 9.99m) =>
        new { Sku = sku, Name = name, Description = "A widget.", Price = price };

    private static async Task<Guid> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/products", body);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    [Fact]
    public async Task GetProducts_WhenAnonymous_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/products");

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "the catalogue is not public, and both reads are ICacheable so they need a caller " +
            "to scope the cache key to");
    }

    [Fact]
    public async Task PostProducts_WhenAnonymous_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/products", NewProduct(NewSku()));

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized, "an anonymous write must be refused before it validates");
    }

    [Fact]
    public async Task PutProducts_WhenAnonymous_Returns401()
    {
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"/api/products/{Guid.NewGuid()}", new { Name = "Widget", Price = 1m });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostProducts_WithAValidRequest_Returns201()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/products", NewProduct(NewSku()));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull(
            "CreatedAtAction must point at the GET route for the new product");
    }

    [Fact]
    public async Task PostProducts_ThenGet_ReturnsTheProduct()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = NewSku();
        var id = await CreateAsync(client, NewProduct(sku, "Widget", 19.95m));

        var response = await client.GetFromJsonAsync<ProductResponseDto>($"/api/products/{id}");

        response.Should().NotBeNull();
        response.Sku.Should().Be(sku);
        response.Name.Should().Be("Widget");
        response.Description.Should().Be("A widget.");
        response.Price.Should().Be(19.95m);
        response.UpdatedAt.Should().Be(response.CreatedAt, "nothing has updated it yet");
    }

    [Fact]
    public async Task PostProducts_NormalizesTheSku()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = NewSku();
        var id = await CreateAsync(client, NewProduct(sku.ToLowerInvariant()));

        var response = await client.GetFromJsonAsync<ProductResponseDto>($"/api/products/{id}");

        response!.Sku.Should().Be(sku);
    }

    [Fact]
    public async Task PostProducts_WithADuplicateSku_Returns409()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = NewSku();
        await CreateAsync(client, NewProduct(sku));

        var response = await client.PostAsJsonAsync("/api/products", NewProduct(sku, "Duplicate"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PostProducts_WithADuplicateSkuInAnotherCase_Returns409()
    {
        // The availability check runs against the normalized form, so case must not slip past it
        // into the unique index and come back as a 500.
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = NewSku();
        await CreateAsync(client, NewProduct(sku));

        var response = await client.PostAsJsonAsync(
            "/api/products", NewProduct(sku.ToLowerInvariant(), "Duplicate"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PostProducts_WithABlankName_Returns400()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/products", NewProduct(NewSku(), name: ""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The 400 must come from CreateProductValidator through result.Problem(), not from
        // [ApiController]'s ModelState - DataAnnotations were deliberately left off
        // CreateProductRequest so the validation behavior is what answers. See src/Api/CLAUDE.md.
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsDto>();
        problem!.Title.Should().Be("validation.failed");
    }

    [Fact]
    public async Task PostProducts_WithANegativePrice_Returns400()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/products", NewProduct(NewSku(), price: -1m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostProducts_WithTooManyDecimalPlaces_Returns400()
    {
        // The column is numeric(18,2); a third decimal would be rounded away silently.
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/products", NewProduct(NewSku(), price: 1.005m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetProduct_WithAnUnknownId_Returns404()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/products/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetProduct_CreatedByAnotherUser_IsVisible()
    {
        // The catalogue is global, unlike orders: a product one caller creates is readable by
        // every other signed-in caller. This is the test that would fail if an owner filter
        // were ever added to the read path.
        using var author = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateAsync(author, NewProduct(NewSku()));

        using var other = await factory.CreateAuthenticatedClientAsync();
        var response = await other.GetAsync($"/api/products/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PutProducts_WithAValidRequest_Returns204AndApplies()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = NewSku();
        var id = await CreateAsync(client, NewProduct(sku));

        var response = await client.PutAsJsonAsync(
            $"/api/products/{id}",
            new { Name = "Gadget", Description = (string?)null, Price = 24.50m });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var reread = await client.GetFromJsonAsync<ProductResponseDto>($"/api/products/{id}");
        reread!.Name.Should().Be("Gadget");
        reread.Description.Should().BeNull();
        reread.Price.Should().Be(24.50m);
        reread.Sku.Should().Be(sku, "the sku is not editable");
        reread.UpdatedAt.Should().BeAfter(reread.CreatedAt);
    }

    [Fact]
    public async Task PutProducts_WithAnUnknownId_Returns404()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/products/{Guid.NewGuid()}", new { Name = "Gadget", Price = 1m });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PutProducts_WithABlankName_Returns400()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var id = await CreateAsync(client, NewProduct(NewSku()));

        var response = await client.PutAsJsonAsync(
            $"/api/products/{id}", new { Name = "", Price = 1m });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetProducts_ReturnsAPageContainingANewProduct()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = NewSku();
        await CreateAsync(client, NewProduct(sku));

        var page = await client.GetFromJsonAsync<ProductPageDto>("/api/products?limit=100");

        page.Should().NotBeNull();
        page.Items.Should().Contain(i => i.Sku == sku);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task GetProducts_WithAnOutOfRangeLimit_Returns400(int limit)
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/products?limit={limit}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetProducts_WithAMalformedCursor_Returns400()
    {
        // A 400, never a 500, and never a silently empty first page.
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/products?cursor=not-a-cursor");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    public sealed record ProductResponseDto(
        Guid Id,
        string Sku,
        string Name,
        string? Description,
        decimal Price,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    public sealed record ProductListItemDto(
        Guid Id, string Sku, string Name, decimal Price, DateTimeOffset CreatedAt);

    public sealed record ProductPageDto(
        IReadOnlyList<ProductListItemDto> Items, string? NextCursor);

    public sealed record ProblemDetailsDto(string? Title, string? Detail, int? Status);
}
