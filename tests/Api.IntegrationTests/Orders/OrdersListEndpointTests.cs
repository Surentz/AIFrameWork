using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Orders;

[Collection(nameof(ApiFactoryCollection))]
public sealed class OrdersListEndpointTests(ApiFactory factory)
{
    [Fact]
    public async Task GetOrders_AfterPlacingAnOrder_ReturnsItInTheList()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(client);
        var created = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = sku, Quantity = 3 });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await created.Content.ReadFromJsonAsync<Guid>();

        var page = await client.GetFromJsonAsync<OrderPageDto>("/api/orders?limit=100");

        page.Should().NotBeNull();
        page.Items.Should().Contain(i => i.Id == id && i.Sku == sku);
    }

    [Fact]
    public async Task GetOrders_WithALimitOfOne_ReturnsOneItemAndACursor()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var skuA = await CatalogueSetup.CreateProductAsync(client);
        var skuB = await CatalogueSetup.CreateProductAsync(client);
        await client.PostAsJsonAsync("/api/orders", new { Sku = skuA, Quantity = 1 });
        await client.PostAsJsonAsync("/api/orders", new { Sku = skuB, Quantity = 1 });

        var page = await client.GetFromJsonAsync<OrderPageDto>("/api/orders?limit=1");

        page.Should().NotBeNull();
        page.Items.Should().HaveCount(1);
        page.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task GetOrders_WhenFollowingTheCursorAfterANewOrderArrives_NeitherRepeatsNorSkipsARow()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        // Each order gets its own product: this shared database also holds rows from every
        // other test in ApiFactoryCollection, so the ids returned - not the row count - are
        // what this test can safely pin down.
        var skuA = await CatalogueSetup.CreateProductAsync(client);
        var skuB = await CatalogueSetup.CreateProductAsync(client);
        var skuC = await CatalogueSetup.CreateProductAsync(client);
        var createdA = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = skuA, Quantity = 1 });
        var idA = await createdA.Content.ReadFromJsonAsync<Guid>();
        var createdB = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = skuB, Quantity = 1 });
        var idB = await createdB.Content.ReadFromJsonAsync<Guid>();

        var pageOne = await client.GetFromJsonAsync<OrderPageDto>("/api/orders?limit=1");
        pageOne.Should().NotBeNull();
        pageOne.Items.Select(i => i.Id).Should().Equal(idB);
        var cursor = pageOne.NextCursor
            ?? throw new InvalidOperationException("expected a next cursor after page one.");

        // The row that arrives while the caller sits on page 1 is the whole point: a client
        // paging a static table would pass identically under correct keyset paging and under
        // broken offset paging. Only a row inserted between the two HTTP calls tells them apart.
        await client.PostAsJsonAsync(
            "/api/orders", new { Sku = skuC, Quantity = 1 });

        var pageTwo = await client.GetFromJsonAsync<OrderPageDto>(
            $"/api/orders?limit=1&cursor={Uri.EscapeDataString(cursor)}");

        pageTwo.Should().NotBeNull();
        pageTwo.Items.Select(i => i.Id).Should().Equal(idA);
        pageTwo.Items.Should().NotContain(i => i.Id == idB);
    }

    [Fact]
    public async Task GetOrders_WithAnOutOfRangeLimit_Returns400()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync(new Uri("/api/orders?limit=0", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString()
            .Should().Be("orders.limit_out_of_range");
    }

    [Fact]
    public async Task GetOrders_WithAMalformedCursor_Returns400NotAServerError()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync(
            new Uri("/api/orders?cursor=not-a-cursor", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString()
            .Should().Be("orders.malformed_cursor");
    }

    [Fact]
    public async Task GetOrders_DoesNotReturnAnotherUsersOrders()
    {
        using var owner = await factory.CreateAuthenticatedClientAsync();
        using var stranger = await factory.CreateAuthenticatedClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(owner);
        var created = await owner.PostAsJsonAsync(
            "/api/orders", new { Sku = sku, Quantity = 1 });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await created.Content.ReadFromJsonAsync<Guid>();

        var page = await stranger.GetFromJsonAsync<OrderPageDto>("/api/orders?limit=100");

        page.Should().NotBeNull();
        page.Items.Should().NotContain(
            i => i.Id == id,
            "another user's orders must never appear in this caller's page");
    }

    public sealed record OrderPageDto(IReadOnlyList<OrderListItemDto> Items, string? NextCursor);

    public sealed record OrderListItemDto(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt);
}
