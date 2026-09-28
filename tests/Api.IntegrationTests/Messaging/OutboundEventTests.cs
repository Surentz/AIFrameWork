using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiFramework.Api.IntegrationTests.Orders;
using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Api.IntegrationTests.Messaging;

/// <summary>Domain events reach RabbitMQ as their versioned integration contracts. ADR 0026.</summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class OutboundEventTests(ApiFactory factory)
{
    // A bounded real-time wait, for the reason BrokerProbe.WaitForMessageAsync gives: delivery is
    // Wolverine's sending agent and then the broker, neither of which an IClock reaches. A passing
    // test returns as soon as its message is there.
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(30);

    private async Task<Guid> PlaceOrderAsync()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(factory);
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = sku, Quantity = 2 });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    [Fact]
    public async Task PlacingAnOrder_PublishesOrderPlacedV1WithItsProperties()
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var queue = await probe.BindTemporaryQueueAsync(RabbitMqTopology.EventsExchange, "order.placed");
        var orderId = await PlaceOrderAsync();

        await factory.DrainOutboxUntilEmptyAsync();
        var message = await FindAsync(probe, queue, "orderId", orderId);

        message.Should().NotBeNull("the durable outbox must deliver the event to a bound queue");
        message!.Value.Type.Should().Be("order.placed.v1");
        message.Value.ContentType.Should().Be("application/json");
        Guid.TryParse(message.Value.MessageId, out _).Should().BeTrue("message_id is the eventId");
        message.Value.Body.GetProperty("eventId").GetGuid().ToString()
            .Should().Be(message.Value.MessageId, "message_id and the body's eventId are the same dedupe key");
        message.Value.Body.GetProperty("quantity").GetInt32().Should().Be(2);
    }

    // correlation_id is the originating request's trace id. Nothing here sets it explicitly:
    // Wolverine's message bus takes Activity.Current's root id, and the bus is resolved inside the
    // delivery Activity OutboxWorkItemProcessor restores from the row's stored traceparent.
    [Fact]
    public async Task PlacingAnOrder_PublishesWithTheRequestsTraceIdAsCorrelationId()
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var queue = await probe.BindTemporaryQueueAsync(RabbitMqTopology.EventsExchange, "order.placed");
        var orderId = await PlaceOrderAsync();

        string? traceParent;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
            traceParent = await context.Outbox.AsNoTracking()
                .Where(m => m.EventName == "order.placed"
                    && m.Payload.Contains(orderId.ToString("D", CultureInfo.InvariantCulture)))
                .Select(m => m.TraceParent)
                .SingleAsync();
        }

        await factory.DrainOutboxUntilEmptyAsync();
        var message = await FindAsync(probe, queue, "orderId", orderId);

        traceParent.Should().NotBeNull("the request that placed the order was traced");
        message.Should().NotBeNull();
        // traceparent is "00-<trace id>-<span id>-<flags>".
        message!.Value.CorrelationId.Should().Be(traceParent.Split('-')[1]);
    }

    // Review Focus 4: an order from before the catalogue link has null product columns.
    [Fact]
    public async Task APlacedEventForALegacyOrder_CarriesNullProductFields()
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var queue = await probe.BindTemporaryQueueAsync(RabbitMqTopology.EventsExchange, "order.placed");
        var orderId = await PlaceOrderAsync();

        // Before draining: the publisher reads the order when the event is delivered, so this makes
        // it read exactly what a pre-catalogue row looks like. Columns per OrderConfiguration; all
        // three are nullable (migration LinkOrdersToTheCatalogue).
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""update orders set "ProductId" = null, "ProductName" = null, "UnitPrice" = null where "Id" = {orderId}""");
        }

        await factory.DrainOutboxUntilEmptyAsync();
        var message = await FindAsync(probe, queue, "orderId", orderId);

        message.Should().NotBeNull();
        message!.Value.Body.GetProperty("productName").ValueKind.Should().Be(JsonValueKind.Null);
        message.Value.Body.GetProperty("unitPrice").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AnEventNobodyBinds_IsKeptInTheUnroutedQueue()
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);

        // product.price_changed has no binding in this test run: a price update is the event.
        using var admin = await factory.CreateAdminClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(factory, price: 10m);
        var products = await admin.GetFromJsonAsync<JsonElement>("/api/products?limit=100");
        var productId = products.GetProperty("items").EnumerateArray()
            .Single(p => string.Equals(p.GetProperty("sku").GetString(), sku, StringComparison.Ordinal))
            .GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync(
                $"/api/products/{productId}", new { Name = "Widget", Description = (string?)null, Price = 12m }))
            .IsSuccessStatusCode.Should().BeTrue();

        await factory.DrainOutboxUntilEmptyAsync();

        // Found by content rather than by a count going up: every event nobody binds lands in this
        // one queue, including other tests' leftovers that this drain also delivered.
        var message = await FindAsync(probe, RabbitMqTopology.UnroutedQueue, "productId", productId);

        message.Should().NotBeNull(
            "an event with no bound consumer must be kept by the alternate exchange, not dropped");
        message!.Value.Type.Should().Be("product.price_changed.v1");
    }

    /// <summary>
    /// The first message on <paramref name="queue"/> whose body has <paramref name="idProperty"/>
    /// equal to <paramref name="id"/>, skipping (and consuming) any other, within <see cref="Delivery"/>.
    /// </summary>
    private static async Task<(string? Type, string? ContentType, string? MessageId, string? CorrelationId, JsonElement Body)?> FindAsync(
        BrokerProbe probe, string queue, string idProperty, Guid id)
    {
        var deadline = DateTime.UtcNow + Delivery;
        while (DateTime.UtcNow < deadline)
        {
            var got = await probe.WaitForMessageAsync(queue, deadline - DateTime.UtcNow);
            if (got is null)
            {
                return null;
            }

            var body = JsonDocument.Parse(Encoding.UTF8.GetString(got.Body.Span)).RootElement.Clone();
            if (body.TryGetProperty(idProperty, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetGuid() == id)
            {
                return (got.BasicProperties.Type, got.BasicProperties.ContentType, got.BasicProperties.MessageId,
                    got.BasicProperties.CorrelationId, body);
            }
        }

        return null;
    }
}
