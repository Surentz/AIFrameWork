using AiFramework.Domain.Orders;

namespace AiFramework.Api.Fulfilment;

public sealed record FulfilmentOrderResponse
{
    public required Guid Id { get; init; }

    public required Guid BuyerId { get; init; }

    /// <summary>Null only if the buyer's account row is missing — see <c>FulfilmentQueueRow</c>.</summary>
    public string? BuyerUsername { get; init; }

    /// <summary>Serialized as its name — see <c>NotificationResponse.Kind</c>.</summary>
    public required OrderStatus Status { get; init; }

    public required string Sku { get; init; }

    public required int Quantity { get; init; }

    public required DateTimeOffset PlacedAt { get; init; }

    /// <summary>
    /// The catalogue product as it was when the order was placed. Null on orders that predate the
    /// catalogue link.
    /// </summary>
    public string? ProductName { get; init; }

    public decimal? UnitPrice { get; init; }
}

public sealed record FulfilmentOrderPageResponse
{
    public required IReadOnlyList<FulfilmentOrderResponse> Items { get; init; }

    public required string? NextCursor { get; init; }
}
