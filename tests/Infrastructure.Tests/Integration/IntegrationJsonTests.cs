using System.Text.Json;
using AiFramework.Application.IntegrationEvents;
using AiFramework.Infrastructure.Integration;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Integration;

/// <summary>
/// The wire contract external systems depend on. A change that breaks one of these is a breaking
/// change to them - which is the point of pinning the shape here. See the messaging skill.
/// </summary>
public sealed class IntegrationJsonTests
{
    private static readonly Guid EventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BuyerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset At = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OrderPlacedV1_SerializesAsCamelCaseJson()
    {
        var json = JsonSerializer.Serialize(
            new OrderPlacedV1(EventId, At, OrderId, BuyerId, "SKU-1", 2, "Widget", 19.95m),
            IntegrationJson.Options);

        json.Should().Be(
            "{\"eventId\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"occurredAt\":\"2026-09-27T12:00:00+00:00\"," +
            "\"orderId\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"buyerId\":\"33333333-3333-3333-3333-333333333333\"," +
            "\"sku\":\"SKU-1\",\"quantity\":2,\"productName\":\"Widget\",\"unitPrice\":19.95}");
    }

    [Fact]
    public void OrderPlacedV1_WithNoProductSnapshot_WritesNulls()
    {
        var json = JsonSerializer.Serialize(
            new OrderPlacedV1(EventId, At, OrderId, BuyerId, "SKU-1", 2, null, null),
            IntegrationJson.Options);

        json.Should().Contain("\"productName\":null").And.Contain("\"unitPrice\":null");
    }

    [Fact]
    public void ShipmentConfirmedV1_FromCamelCase_Deserializes()
    {
        var message = JsonSerializer.Deserialize<ShipmentConfirmedV1>(
            "{\"shipmentId\":\"WH-1\",\"orderId\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"shippedAt\":\"2026-09-27T12:00:00+00:00\"}",
            IntegrationJson.Options);

        message.Should().Be(new ShipmentConfirmedV1("WH-1", OrderId, At));
    }

    // Review Focus 1: an external producer is not ours to format.
    [Fact]
    public void ShipmentConfirmedV1_FromPascalCaseWithExtraFields_Deserializes()
    {
        var message = JsonSerializer.Deserialize<ShipmentConfirmedV1>(
            "{\"ShipmentId\":\"WH-1\",\"OrderId\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"ShippedAt\":\"2026-09-27T12:00:00+00:00\",\"Carrier\":\"DHL\"}",
            IntegrationJson.Options);

        message.Should().Be(new ShipmentConfirmedV1("WH-1", OrderId, At));
    }

    [Theory]
    [InlineData(typeof(OrderPlacedV1), "order.placed.v1", "order.placed")]
    [InlineData(typeof(OrderShippedV1), "order.shipped.v1", "order.shipped")]
    [InlineData(typeof(OrderCancelledV1), "order.cancelled.v1", "order.cancelled")]
    [InlineData(typeof(ProductPriceChangedV1), "product.price_changed.v1", "product.price_changed")]
    public void EachOutboundContract_HasItsVersionedNameAndRoutingKey(
        Type contract, string typeName, string routingKey)
    {
        var names = (ValueTuple<string, string>)typeof(IntegrationJsonTests)
            .GetMethod(nameof(NamesOf), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(contract)
            .Invoke(null, null)!;

        names.Should().Be((typeName, routingKey));
    }

    // Reads the static abstract members through a generic, the only way to reach them.
    private static (string, string) NamesOf<T>() where T : IIntegrationEvent => (T.TypeName, T.RoutingKey);

    [Fact]
    public void Options_IsReadOnly()
    {
        IntegrationJson.Options.IsReadOnly.Should().BeTrue(
            "shared, mutable JsonSerializerOptions is a footgun - a caller must copy before changing it");
    }

    // Wolverine's SystemTextJsonSerializer adds a converter to the options instance it is given,
    // which throws on read-only options - callers must construct
    // new JsonSerializerOptions(IntegrationJson.Options) rather than pass the shared instance.
    [Fact]
    public void CopyOfOptions_IsWritable()
    {
        var copy = new JsonSerializerOptions(IntegrationJson.Options);

        copy.IsReadOnly.Should().BeFalse();
        var act = () => copy.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        act.Should().NotThrow();
    }
}
