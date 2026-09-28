using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;
using Microsoft.Extensions.Logging;

namespace AiFramework.Application.IntegrationEvents;

// One per domain event, each mapping to its versioned contract. Idempotent by construction: they
// write nothing of their own, and a redelivery republishes with the SAME EventId, which is what
// consumers deduplicate on. Delivery is at least once - see the messaging skill.

public sealed partial class OrderPlacedIntegrationPublisher(
    IOrderRepository orders, IIntegrationEventPublisher publisher, ILogger<OrderPlacedIntegrationPublisher> logger)
    : IDomainEventHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // OrderPlaced predates the notification feed and carries no buyer; the order's snapshot
        // fields never change after placement, so reading them late is safe.
        var order = await orders.GetForPublishingAsync(domainEvent.OrderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            // Nothing to describe. The order was placed in this same transaction, so this is not
            // expected - and returning quietly would make order.placed.v1 vanish without a trace.
            // The id only: never the event instance (root CLAUDE.md, Logging).
            LogOrderMissing(logger, domainEvent.OrderId);
            return;
        }

        await publisher.PublishAsync(
            new OrderPlacedV1(
                context.MessageId, context.OccurredAt, order.Id, order.UserId, order.Sku, order.Quantity,
                order.Product?.Name, order.Product?.UnitPrice),
            cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Order {OrderId} was not found, so no order.placed.v1 was published for it.")]
    private static partial void LogOrderMissing(ILogger logger, Guid orderId);
}

public sealed class OrderShippedIntegrationPublisher(IIntegrationEventPublisher publisher)
    : IDomainEventHandler<OrderShipped>
{
    public Task HandleAsync(OrderShipped domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return publisher.PublishAsync(
            new OrderShippedV1(context.MessageId, context.OccurredAt, domainEvent.OrderId, domainEvent.UserId, domainEvent.Sku),
            cancellationToken);
    }
}

public sealed class OrderCancelledIntegrationPublisher(IIntegrationEventPublisher publisher)
    : IDomainEventHandler<OrderCancelled>
{
    public Task HandleAsync(OrderCancelled domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return publisher.PublishAsync(
            new OrderCancelledV1(context.MessageId, context.OccurredAt, domainEvent.OrderId, domainEvent.UserId,
                domainEvent.Sku, domainEvent.Reason),
            cancellationToken);
    }
}

public sealed class ProductPriceChangedIntegrationPublisher(IIntegrationEventPublisher publisher)
    : IDomainEventHandler<ProductPriceChanged>
{
    public Task HandleAsync(ProductPriceChanged domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return publisher.PublishAsync(
            new ProductPriceChangedV1(context.MessageId, context.OccurredAt, domainEvent.ProductId, domainEvent.Sku,
                domainEvent.Name, domainEvent.OldPrice, domainEvent.NewPrice),
            cancellationToken);
    }
}
