using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Orders;

/// <summary>
/// The operator's side of an order, over HTTP, against the real policy and the real per-request
/// role read. ADR 0024.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class FulfilmentEndpointTests(ApiFactory factory)
{
    private sealed record StatusResponse(Guid OrderId, string Status, DateTimeOffset ChangedAt);

    private sealed record QueueItem(
        Guid Id, Guid BuyerId, string? BuyerUsername, string Status, string Sku, int Quantity,
        DateTimeOffset PlacedAt);

    private sealed record QueuePage(IReadOnlyList<QueueItem> Items, string? NextCursor);

    private sealed record OrderItem(Guid Id, string Status);

    private sealed record NotificationItem(Guid Id, string Kind, Guid? SubjectId);

    private sealed record NotificationPage(IReadOnlyList<NotificationItem> Items, string? NextCursor);

    private async Task<Guid> PlaceOrderAsync(HttpClient client)
    {
        var sku = await CatalogueSetup.CreateProductAsync(factory);
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = sku, Quantity = 2 });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    /// <summary>
    /// Walks the queue until <paramref name="orderId"/> appears. The queue is every test's orders
    /// in one shared database, oldest first, so a fresh order is rarely on page one.
    /// </summary>
    private static async Task<QueueItem?> FindInQueueAsync(HttpClient client, Guid orderId, string status)
    {
        string? cursor = null;
        do
        {
            var url = $"/api/fulfilment/orders?status={status}&limit=100"
                + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await client.GetFromJsonAsync<QueuePage>(url);
            page.Should().NotBeNull();

            if (page.Items.FirstOrDefault(i => i.Id == orderId) is { } found)
            {
                return found;
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return null;
    }

    [Fact]
    public async Task ListQueue_AsAMember_IsForbidden()
    {
        using var member = await factory.CreateAuthenticatedClientAsync();

        var response = await member.GetAsync("/api/fulfilment/orders");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Ship_AsAMember_IsForbidden()
    {
        using var member = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(member);

        var response = await member.PostAsync($"/api/fulfilment/orders/{orderId}/ship", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Ship_OnTheOldBuyerRoute_IsGone()
    {
        using var buyer = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(buyer);

        var response = await buyer.PostAsync($"/api/orders/{orderId}/ship", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ship_AsAnAdministrator_ShipsAnotherUsersOrder()
    {
        using var buyer = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(buyer);
        using var admin = await factory.CreateAdminClientAsync();

        var response = await admin.PostAsync($"/api/fulfilment/orders/{orderId}/ship", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<StatusResponse>())!.Status.Should().Be("Shipped");
    }

    [Fact]
    public async Task Ship_AsAnAdministrator_IsVisibleToTheBuyer()
    {
        // The cache is off under test, so this proves the write persisted, not freshness: in
        // production the buyer's cached read may lag by its TTL. ADR 0024.
        using var buyer = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(buyer);
        using var admin = await factory.CreateAdminClientAsync();

        await admin.PostAsync($"/api/fulfilment/orders/{orderId}/ship", null);

        var order = await buyer.GetFromJsonAsync<OrderItem>($"/api/orders/{orderId}");
        order!.Status.Should().Be("Shipped");
    }

    [Fact]
    public async Task Ship_Twice_IsConflict()
    {
        using var buyer = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(buyer);
        using var admin = await factory.CreateAdminClientAsync();
        await admin.PostAsync($"/api/fulfilment/orders/{orderId}/ship", null);

        var second = await admin.PostAsync($"/api/fulfilment/orders/{orderId}/ship", null);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Ship_AnUnknownOrder_IsNotFound()
    {
        using var admin = await factory.CreateAdminClientAsync();

        var response = await admin.PostAsync($"/api/fulfilment/orders/{Guid.NewGuid()}/ship", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ShippingAndDraining_NotifiesTheBuyer()
    {
        using var buyer = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(buyer);
        using var admin = await factory.CreateAdminClientAsync();
        await admin.PostAsync($"/api/fulfilment/orders/{orderId}/ship", null);

        await factory.DrainOutboxUntilEmptyAsync();

        var page = await buyer.GetFromJsonAsync<NotificationPage>("/api/notifications?limit=100");
        page!.Items.Should().Contain(n => n.SubjectId == orderId && n.Kind == "OrderShipped");
    }

    [Fact]
    public async Task ListQueue_AsAnAdministrator_IncludesAnotherUsersOrderWithTheirUsername()
    {
        var (buyer, username) = await factory.CreateAuthenticatedClientWithUsernameAsync();
        using (buyer)
        {
            var orderId = await PlaceOrderAsync(buyer);
            using var admin = await factory.CreateAdminClientAsync();

            var item = await FindInQueueAsync(admin, orderId, "Placed");

            item.Should().NotBeNull();
            item.BuyerUsername.Should().Be(username);
        }
    }

    [Fact]
    public async Task ListQueue_AfterShipping_NoLongerListsTheOrderAsPlaced()
    {
        using var buyer = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(buyer);
        using var admin = await factory.CreateAdminClientAsync();
        await admin.PostAsync($"/api/fulfilment/orders/{orderId}/ship", null);

        var item = await FindInQueueAsync(admin, orderId, "Placed");

        item.Should().BeNull();
    }

    [Fact]
    public async Task ListQueue_WithAMalformedCursor_IsBadRequest()
    {
        using var admin = await factory.CreateAdminClientAsync();

        var response = await admin.GetAsync("/api/fulfilment/orders?cursor=not-a-cursor");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
