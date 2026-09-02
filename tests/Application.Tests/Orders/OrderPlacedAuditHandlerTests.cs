using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

public sealed class OrderPlacedAuditHandlerTests
{
    private readonly IOrderAuditWriter _writer = Substitute.For<IOrderAuditWriter>();

    [Fact]
    public async Task HandleAsync_RecordsTheAuditKeyedOnTheMessageId()
    {
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var handler = new OrderPlacedAuditHandler(_writer);

        await handler.HandleAsync(
            new OrderPlaced(orderId, "SKU-1", 2),
            new DomainEventContext(messageId, 1),
            CancellationToken.None);

        await _writer.Received(1).RecordAsync(messageId, orderId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CalledTwiceWithTheSameMessageId_RecordsWithTheSameKey()
    {
        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var handler = new OrderPlacedAuditHandler(_writer);
        var context = new DomainEventContext(messageId, 1);
        var raised = new OrderPlaced(orderId, "SKU-1", 2);

        await handler.HandleAsync(raised, context, CancellationToken.None);
        await handler.HandleAsync(raised, new DomainEventContext(messageId, 2), CancellationToken.None);

        // Both calls use the same key, so the writer's insert-if-absent makes the second a
        // no-op. The handler carries no dedupe state of its own; the key is the whole mechanism.
        await _writer.Received(2).RecordAsync(messageId, orderId, Arg.Any<CancellationToken>());
    }
}
