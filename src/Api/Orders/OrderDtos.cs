namespace AiFramework.Api.Orders;

/// <summary>
/// Business-rule validation (non-empty, max length, positive quantity) is owned by
/// <c>PlaceOrderValidator</c> in the validation behavior — not duplicated here as
/// DataAnnotations. See <c>src/Api/CLAUDE.md</c>.
/// </summary>
public sealed record PlaceOrderRequest
{
    public required string Sku { get; init; }

    public required int Quantity { get; init; }
}

public sealed record OrderResponse
{
    public required Guid Id { get; init; }

    public required string Sku { get; init; }

    public required int Quantity { get; init; }

    public required DateTimeOffset PlacedAt { get; init; }

    /// <summary>
    /// The catalogue product as it was when the order was placed. Null on orders that predate the
    /// catalogue link — those render as the bare sku with no price.
    /// </summary>
    public Guid? ProductId { get; init; }

    public string? ProductName { get; init; }

    public decimal? UnitPrice { get; init; }
}

public sealed record OrderListItemResponse
{
    public required Guid Id { get; init; }

    public required string Sku { get; init; }

    public required int Quantity { get; init; }

    public required DateTimeOffset PlacedAt { get; init; }

    /// <summary>
    /// The catalogue product as it was when the order was placed. Null on orders that predate the
    /// catalogue link — those render as the bare sku with no price.
    /// </summary>
    public Guid? ProductId { get; init; }

    public string? ProductName { get; init; }

    public decimal? UnitPrice { get; init; }
}

public sealed record OrderPageResponse
{
    public required IReadOnlyList<OrderListItemResponse> Items { get; init; }

    public required string? NextCursor { get; init; }
}
