namespace AiFramework.Application.IntegrationEvents;

/// <summary>
/// <c>shipment.confirmed.v1</c>, received on <c>aiframework.shipments</c> from a warehouse.
/// </summary>
/// <param name="ShipmentId">The warehouse's own reference - a string, because it is theirs.</param>
/// <param name="OrderId">The order this shipment fulfils.</param>
/// <param name="ShippedAt">When the warehouse dispatched it.</param>
public sealed record ShipmentConfirmedV1(string ShipmentId, Guid OrderId, DateTimeOffset ShippedAt)
{
    public const string TypeName = "shipment.confirmed.v1";
}
