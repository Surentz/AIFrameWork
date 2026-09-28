using AiFramework.Application.Abstractions;
using AiFramework.Application.IntegrationEvents;
using AiFramework.Application.Orders;
using AiFramework.Application.Tests.Orders;
using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.IntegrationEvents;

public sealed class IntegrationPublisherTests
{
    private static readonly Guid MessageId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DomainEventContext Context = new(MessageId, 1, OccurredAt);

    private readonly IIntegrationEventPublisher _publisher = Substitute.For<IIntegrationEventPublisher>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();

    [Fact]
    public async Task HandleAsync_OrderPlaced_PublishesTheSnapshotWithTheOutboxMessageIdAsEventId()
    {
        var buyer = Guid.NewGuid();
        var order = Order.Place(Guid.NewGuid(), buyer, 2, OccurredAt, AnOrderedProduct.Any(), "SKU-1");
        _orders.GetForPublishingAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        await new OrderPlacedIntegrationPublisher(_orders, _publisher).HandleAsync(
            new OrderPlaced(order.Id, "SKU-1", 2), Context, CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            new OrderPlacedV1(MessageId, OccurredAt, order.Id, buyer, "SKU-1", 2,
                order.Product!.Name, order.Product.UnitPrice), // non-null: AnOrderedProduct.Any() supplies one
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_OrderPlacedForAnOrderThatNoLongerExists_PublishesNothing()
    {
        _orders.GetForPublishingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);

        await new OrderPlacedIntegrationPublisher(_orders, _publisher).HandleAsync(
            new OrderPlaced(Guid.NewGuid(), "SKU", 1), Context, CancellationToken.None);

        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync<OrderPlacedV1>(default!, default);
    }

    [Fact]
    public async Task HandleAsync_OrderShipped_PublishesOrderShippedV1()
    {
        var orderId = Guid.NewGuid();
        var buyer = Guid.NewGuid();

        await new OrderShippedIntegrationPublisher(_publisher).HandleAsync(
            new OrderShipped(orderId, buyer, "SKU-1"), Context, CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            new OrderShippedV1(MessageId, OccurredAt, orderId, buyer, "SKU-1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_OrderCancelled_PublishesTheReason()
    {
        var orderId = Guid.NewGuid();
        var buyer = Guid.NewGuid();

        await new OrderCancelledIntegrationPublisher(_publisher).HandleAsync(
            new OrderCancelled(orderId, buyer, "SKU-1", "Out of stock."), Context, CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            new OrderCancelledV1(MessageId, OccurredAt, orderId, buyer, "SKU-1", "Out of stock."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ProductPriceChanged_PublishesBothPrices()
    {
        var productId = Guid.NewGuid();

        await new ProductPriceChangedIntegrationPublisher(_publisher).HandleAsync(
            new ProductPriceChanged(productId, "SKU-1", "Widget", 10m, 12.5m), Context, CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            new ProductPriceChangedV1(MessageId, OccurredAt, productId, "SKU-1", "Widget", 10m, 12.5m),
            Arg.Any<CancellationToken>());
    }
}
