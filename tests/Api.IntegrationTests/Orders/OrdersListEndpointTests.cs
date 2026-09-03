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
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = "SKU-LIST-1", Quantity = 3 });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await created.Content.ReadFromJsonAsync<Guid>();

        var page = await client.GetFromJsonAsync<OrderPageDto>("/api/orders?limit=100");

        page.Should().NotBeNull();
        page.Items.Should().Contain(i => i.Id == id && i.Sku == "SKU-LIST-1");
    }

    [Fact]
    public async Task GetOrders_WithALimitOfOne_ReturnsOneItemAndACursor()
    {
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-LIST-2", Quantity = 1 });
        await client.PostAsJsonAsync("/api/orders", new { Sku = "SKU-LIST-3", Quantity = 1 });

        var page = await client.GetFromJsonAsync<OrderPageDto>("/api/orders?limit=1");

        page.Should().NotBeNull();
        page.Items.Should().HaveCount(1);
        page.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task GetOrders_WhenFollowingTheCursorAfterANewOrderArrives_NeitherRepeatsNorSkipsARow()
    {
        using var client = factory.CreateClient();

        // Unique per run, not "SKU-LIST-N": this shared database also holds rows from every
        // other test in ApiFactoryCollection, so the ids returned - not the row count - are
        // what this test can safely pin down.
        var suffix = Guid.NewGuid().ToString("N");
        var createdA = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = $"SKU-CURSOR-A-{suffix}", Quantity = 1 });
        var idA = await createdA.Content.ReadFromJsonAsync<Guid>();
        var createdB = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = $"SKU-CURSOR-B-{suffix}", Quantity = 1 });
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
            "/api/orders", new { Sku = $"SKU-CURSOR-C-{suffix}", Quantity = 1 });

        var pageTwo = await client.GetFromJsonAsync<OrderPageDto>(
            $"/api/orders?limit=1&cursor={Uri.EscapeDataString(cursor)}");

        pageTwo.Should().NotBeNull();
        pageTwo.Items.Select(i => i.Id).Should().Equal(idA);
        pageTwo.Items.Should().NotContain(i => i.Id == idB);
    }

    [Fact]
    public async Task GetOrders_WithAnOutOfRangeLimit_Returns400()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/orders?limit=0", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString()
            .Should().Be("orders.limit_out_of_range");
    }

    [Fact]
    public async Task GetOrders_WithAMalformedCursor_Returns400NotAServerError()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            new Uri("/api/orders?cursor=not-a-cursor", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString()
            .Should().Be("orders.malformed_cursor");
    }

    public sealed record OrderPageDto(IReadOnlyList<OrderListItemDto> Items, string? NextCursor);

    public sealed record OrderListItemDto(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt);
}
