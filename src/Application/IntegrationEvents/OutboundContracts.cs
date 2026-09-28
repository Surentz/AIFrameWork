namespace AiFramework.Application.IntegrationEvents;

/// <summary><c>order.placed.v1</c>. ProductName/UnitPrice are null for orders that predate the catalogue.</summary>
public sealed record OrderPlacedV1(
    Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid BuyerId, string Sku, int Quantity,
    string? ProductName, decimal? UnitPrice) : IIntegrationEvent
{
    public static string TypeName => "order.placed.v1";

    public static string RoutingKey => "order.placed";
}

/// <summary><c>order.shipped.v1</c>.</summary>
public sealed record OrderShippedV1(
    Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid BuyerId, string Sku) : IIntegrationEvent
{
    public static string TypeName => "order.shipped.v1";

    public static string RoutingKey => "order.shipped";
}

/// <summary><c>order.cancelled.v1</c>, carrying the buyer's reason.</summary>
public sealed record OrderCancelledV1(
    Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid BuyerId, string Sku, string Reason)
    : IIntegrationEvent
{
    public static string TypeName => "order.cancelled.v1";

    public static string RoutingKey => "order.cancelled";
}

/// <summary><c>product.price_changed.v1</c>.</summary>
public sealed record ProductPriceChangedV1(
    Guid EventId, DateTimeOffset OccurredAt, Guid ProductId, string Sku, string Name,
    decimal OldPrice, decimal NewPrice) : IIntegrationEvent
{
    public static string TypeName => "product.price_changed.v1";

    public static string RoutingKey => "product.price_changed";
}
