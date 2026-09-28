namespace AiFramework.Application.IntegrationEvents;

/// <summary>
/// An event published to OTHER systems. Deliberately separate from <c>IDomainEvent</c>: a domain
/// event may change shape freely; an integration event is a contract someone else depends on.
/// </summary>
/// <remarks>
/// Versioning: fields may be ADDED within a version. Renaming or removing one means a new type
/// (<c>…V2</c>, <c>"….v2"</c>) published alongside the old until consumers move. ADR 0026.
/// </remarks>
public interface IIntegrationEvent
{
    /// <summary>The hand-built outbox's MessageId: stable across redelivery, the consumer's dedupe key.</summary>
    public Guid EventId { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>The AMQP <c>type</c> property, e.g. <c>order.placed.v1</c>.</summary>
    public static abstract string TypeName { get; }

    /// <summary>The routing key on <c>aiframework.events</c>, e.g. <c>order.placed</c>.</summary>
    public static abstract string RoutingKey { get; }
}
