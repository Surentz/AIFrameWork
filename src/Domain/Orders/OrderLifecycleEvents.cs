using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Orders;

/// <summary>
/// Carries UserId, unlike <see cref="OrderPlaced"/>, which does not. That asymmetry is
/// deliberate rather than an oversight to tidy up: OrderPlaced predates the notification feed
/// and its existing consumer (OrderPlacedAuditHandler) does not need a recipient, while these
/// two exist FOR the feed and a handler reading them must know who to notify. Adding the field
/// to OrderPlaced instead would change the payload shape of events already sitting in the outbox
/// table, which deserialize by name into the old shape.
/// </summary>
public sealed record OrderShipped(Guid OrderId, Guid UserId, string Sku) : IDomainEvent;

/// <summary>See <see cref="OrderShipped"/> for why this carries UserId and OrderPlaced does not.</summary>
public sealed record OrderCancelled(Guid OrderId, Guid UserId, string Sku, string Reason) : IDomainEvent;
