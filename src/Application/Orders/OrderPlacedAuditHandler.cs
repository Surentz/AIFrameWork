using AiFramework.Application.Abstractions;
using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

/// <summary>Writes an audit row if one does not already exist for this message.</summary>
public interface IOrderAuditWriter
{
    public Task RecordAsync(Guid messageId, Guid orderId, CancellationToken cancellationToken);
}

/// <summary>
/// Idempotent by construction: the outbox MessageId is the audit row's primary key, so a
/// redelivery inserts nothing rather than duplicating. Delivery is at-least-once and retry
/// granularity is the message, so this handler WILL run twice at some point.
/// </summary>
public sealed class OrderPlacedAuditHandler(IOrderAuditWriter writer)
    : IDomainEventHandler<OrderPlaced>
{
    public Task HandleAsync(
        OrderPlaced domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return writer.RecordAsync(context.MessageId, domainEvent.OrderId, cancellationToken);
    }
}
