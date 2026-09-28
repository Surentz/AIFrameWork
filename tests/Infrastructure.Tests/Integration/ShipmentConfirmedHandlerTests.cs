using AiFramework.Application.Abstractions;
using AiFramework.Application.IntegrationEvents;
using AiFramework.Application.Orders;
using AiFramework.Infrastructure.Integration;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.Integration;

/// <summary>
/// The rejection message, which an operator reads on the monitoring page's dead letters. Delivery
/// and dead-lettering themselves are Worker.IntegrationTests' ShipmentInboundTests.
/// </summary>
public sealed class ShipmentConfirmedHandlerTests
{
    private static readonly Error Rejected = new(ErrorKind.Validation, "validation", "One or more fields are invalid.");

    private readonly ICommandDispatcher _commands = Substitute.For<ICommandDispatcher>();

    public ShipmentConfirmedHandlerTests() =>
        _commands.SendAsync(Arg.Any<ICommand<ShipmentOutcome>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Failure<ShipmentOutcome>(Rejected)));

    private Func<Task> Handling(string shipmentId, Guid orderId) => () =>
        new ShipmentConfirmedHandler(_commands).Handle(
            new ShipmentConfirmedV1(shipmentId, orderId, DateTimeOffset.UtcNow), CancellationToken.None);

    [Fact]
    public async Task Handle_ARejectedShipment_NamesTheShipmentTheOrderAndTheReason()
    {
        var orderId = Guid.NewGuid();

        var thrown = await Handling("WH-1", orderId).Should().ThrowAsync<IntegrationMessageRejectedException>();

        thrown.Which.Message.Should().Be(
            $"shipment WH-1 for order {orderId} rejected: validation - One or more fields are invalid.");
    }

    // The producer controls the id and the wire does not bound it; the message reaches the logs,
    // wolverine_dead_letters and the monitoring page.
    [Fact]
    public async Task Handle_ARejectedShipmentWithAnOverLongId_QuotesOnlyTheValidatorsLimit()
    {
        var orderId = Guid.NewGuid();
        var limit = ShipmentConfirmedHandler.MaximumQuotedShipmentIdLength;

        var thrown = await Handling(new string('x', 100_000), orderId)
            .Should().ThrowAsync<IntegrationMessageRejectedException>();

        thrown.Which.Message.Should().Be(
            $"shipment {new string('x', limit)}... for order {orderId} rejected: " +
            "validation - One or more fields are invalid.");
    }
}
