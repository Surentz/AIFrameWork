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
}

public sealed record OrderListItemResponse
{
    public required Guid Id { get; init; }

    public required string Sku { get; init; }

    public required int Quantity { get; init; }

    public required DateTimeOffset PlacedAt { get; init; }
}

public sealed record OrderPageResponse
{
    public required IReadOnlyList<OrderListItemResponse> Items { get; init; }

    public required string? NextCursor { get; init; }
}
