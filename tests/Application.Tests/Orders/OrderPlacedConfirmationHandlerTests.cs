using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

/// <summary>
/// The reference for enqueuing a job that must not be lost: off the committed domain event, never
/// from the command handler. See ADR 0016 and <see cref="IJobScheduler"/>'s own remarks.
/// </summary>
public sealed class OrderPlacedConfirmationHandlerTests
{
    private readonly IJobScheduler _jobs = Substitute.For<IJobScheduler>();

    [Fact]
    public async Task HandleAsync_EnqueuesTheConfirmationCarryingTheEventsDetails()
    {
        var domainEvent = new OrderPlaced(Guid.NewGuid(), "SKU-1", 3);
        var handler = new OrderPlacedConfirmationHandler(_jobs);

        await handler.HandleAsync(
            domainEvent, new DomainEventContext(Guid.NewGuid(), Attempt: 1), CancellationToken.None);

        // The job carries the details rather than just the id, so its handler needs no database
        // read — a light job that opens a connection is not a light job.
        await _jobs.Received(1).EnqueueAsync(
            new SendOrderConfirmation(domainEvent.OrderId, "SKU-1", 3),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Delivery off the outbox is at-least-once, so this handler runs twice at some point and
    /// enqueues twice. That is deliberate at this layer — deduping belongs in the notifier, keyed
    /// the way <c>OrderAuditWriter</c> keys its row — and this pins the decision so a later
    /// "fix" here is a conscious one.
    /// </summary>
    [Fact]
    public async Task HandleAsync_OnRedelivery_EnqueuesAgain()
    {
        var domainEvent = new OrderPlaced(Guid.NewGuid(), "SKU-1", 1);
        var messageId = Guid.NewGuid();
        var handler = new OrderPlacedConfirmationHandler(_jobs);

        await handler.HandleAsync(
            domainEvent, new DomainEventContext(messageId, Attempt: 1), CancellationToken.None);
        await handler.HandleAsync(
            domainEvent, new DomainEventContext(messageId, Attempt: 2), CancellationToken.None);

        await _jobs.Received(2).EnqueueAsync(
            Arg.Any<SendOrderConfirmation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public Task HandleAsync_WithANullEvent_Throws()
    {
        var handler = new OrderPlacedConfirmationHandler(_jobs);

        // Not async/await: the single await would be the assertion itself, which AsyncFixer01
        // asks to be returned directly rather than wrapped in a state machine.
        var act = () => handler.HandleAsync(
            null!, new DomainEventContext(Guid.NewGuid(), Attempt: 1), CancellationToken.None);

        return act.Should().ThrowAsync<ArgumentNullException>();
    }
}
