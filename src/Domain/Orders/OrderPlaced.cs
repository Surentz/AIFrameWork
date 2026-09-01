using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Orders;

public sealed record OrderPlaced(Guid OrderId, string Sku, int Quantity) : IDomainEvent;
