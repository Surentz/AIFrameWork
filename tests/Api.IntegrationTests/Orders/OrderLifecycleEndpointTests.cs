using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Orders;

/// <summary>
/// The buyer's side of the lifecycle over HTTP — cancelling — and the notifications it produces
/// once the outbox drains. Shipping is the operator's: see <c>FulfilmentEndpointTests</c>.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class OrderLifecycleEndpointTests(ApiFactory factory)
{
    private sealed record StatusResponse(Guid OrderId, string Status, DateTimeOffset ChangedAt);

    private sealed record NotificationItem(
        Guid Id, string Kind, string Title, string Body, Guid? SubjectId,
        DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

    private sealed record NotificationPage(IReadOnlyList<NotificationItem> Items, string? NextCursor);

    private async Task<Guid> PlaceOrderAsync(HttpClient client)
    {
        var sku = await CatalogueSetup.CreateProductAsync(factory);
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = sku, Quantity = 2 });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    private static async Task<IReadOnlyList<NotificationItem>> NotificationsForAsync(
        HttpClient client, Guid subjectId)
    {
        var page = await client.GetFromJsonAsync<NotificationPage>("/api/notifications?limit=100");
        page.Should().NotBeNull();
        return [.. page.Items.Where(n => n.SubjectId == subjectId)];
    }

    [Fact]
    public async Task CancelOrder_AfterShipping_IsConflict()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(client);
        using var operatorClient = await factory.CreateAdminClientAsync();
        (await operatorClient.PostAsync($"/api/fulfilment/orders/{orderId}/ship", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/cancel", new { Reason = "Changed my mind." });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CancelOrder_OnAPlacedOrder_ReportsCancelled()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/cancel", new { Reason = "Out of stock." });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<StatusResponse>();
        status!.Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task CancelOrder_WithNoReason_IsBadRequest()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/cancel", new { Reason = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CancellingAndDraining_NotifiesTheBuyerWithTheReason()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var orderId = await PlaceOrderAsync(client);
        await client.PostAsJsonAsync(
            $"/api/orders/{orderId}/cancel", new { Reason = "Out of stock." });

        await factory.DrainOutboxUntilEmptyAsync();

        var notifications = await NotificationsForAsync(client, orderId);
        notifications.Should().ContainSingle(n => n.Kind == "OrderCancelled")
            .Which.Body.Should().Contain("Out of stock.");
    }

    [Fact]
    public async Task UpdatingAProductsPriceAndDraining_NotifiesItsPastPurchasers()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(factory, price: 10.00m);

        var placed = await client.PostAsJsonAsync("/api/orders", new { Sku = sku, Quantity = 1 });
        placed.StatusCode.Should().Be(HttpStatusCode.Created);

        var products = await client.GetFromJsonAsync<ProductPage>("/api/products?limit=100");
        products.Should().NotBeNull();
        var product = products.Items.Single(
            p => string.Equals(p.Sku, sku, StringComparison.Ordinal));

        // The operator reprices; the buyer is the one notified. ADR 0025.
        using var admin = await factory.CreateAdminClientAsync();
        var updated = await admin.PutAsJsonAsync(
            $"/api/products/{product.Id}",
            new { Name = "Widget", Description = (string?)null, Price = 12.50m });
        updated.IsSuccessStatusCode.Should().BeTrue();

        await factory.DrainOutboxUntilEmptyAsync();

        var notifications = await NotificationsForAsync(client, product.Id);
        notifications.Should().ContainSingle(n => n.Kind == "ProductPriceChanged")
            .Which.Body.Should().Contain("rose");
    }

    [Fact]
    public async Task UpdatingAProductWithoutChangingThePrice_NotifiesNobody()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(factory, price: 10.00m);
        await client.PostAsJsonAsync("/api/orders", new { Sku = sku, Quantity = 1 });

        var products = await client.GetFromJsonAsync<ProductPage>("/api/products?limit=100");
        products.Should().NotBeNull();
        var product = products.Items.Single(
            p => string.Equals(p.Sku, sku, StringComparison.Ordinal));

        // Asserted, because a refused update would notify nobody too, and pass this for the wrong
        // reason.
        using var admin = await factory.CreateAdminClientAsync();
        var updated = await admin.PutAsJsonAsync(
            $"/api/products/{product.Id}",
            new { Name = "Renamed", Description = (string?)null, Price = 10.00m });
        updated.IsSuccessStatusCode.Should().BeTrue();

        await factory.DrainOutboxUntilEmptyAsync();

        var notifications = await NotificationsForAsync(client, product.Id);
        notifications.Should().BeEmpty();
    }

    private sealed record ProductItem(Guid Id, string Sku, string Name, decimal Price);

    private sealed record ProductPage(IReadOnlyList<ProductItem> Items, string? NextCursor);
}
