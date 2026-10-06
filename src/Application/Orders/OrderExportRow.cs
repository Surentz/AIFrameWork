using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

/// <summary>One order as the export reads it.</summary>
public sealed record OrderExportRow(
    Guid OrderId,
    string Sku,
    string? ProductName,
    int Quantity,
    decimal? UnitPrice,
    OrderStatus Status,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? CancelledAt,
    string? CancellationReason);
