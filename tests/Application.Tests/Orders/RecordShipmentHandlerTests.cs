using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using FluentValidation.TestHelper;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

/// <summary>
/// A warehouse's shipment confirmation. Unlike the operator's ShipOrder, a redelivery must
/// succeed, and the time is the warehouse's rather than the clock's. ADR 0026.
/// </summary>
public sealed class RecordShipmentHandlerTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public RecordShipmentHandlerTests() => _clock.UtcNow.Returns(Now);

    private Order Stored()
    {
        var order = Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1, PlacedAt, AnOrderedProduct.Any(), "SKU-1");
        _orders.GetForFulfilmentAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        return order;
    }

    private RecordShipmentHandler Handler() => new(_orders);

    [Fact]
    public async Task HandleAsync_OnAPlacedOrder_ShipsItAtTheWarehousesTime()
    {
        var order = Stored();
        var shippedAt = Now.AddMinutes(-5);

        var result = await Handler().HandleAsync(new RecordShipment(order.Id, "WH-1", shippedAt), CancellationToken.None);

        result.Value.Should().Be(ShipmentOutcome.Shipped);
        order.Status.Should().Be(OrderStatus.Shipped);
        order.ShippedAt.Should().Be(shippedAt);
    }

    [Fact]
    public async Task HandleAsync_OnAnAlreadyShippedOrder_IsANoOpSuccess()
    {
        var order = Stored();
        order.Ship(Now.AddMinutes(-30));

        var result = await Handler().HandleAsync(new RecordShipment(order.Id, "WH-2", Now), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a redelivered or duplicate confirmation must not fail");
        result.Value.Should().Be(ShipmentOutcome.AlreadyShipped);
        order.ShippedAt.Should().Be(Now.AddMinutes(-30), "the first shipment's time stands");
    }

    [Fact]
    public async Task HandleAsync_OnACancelledOrder_IsAConflict()
    {
        var order = Stored();
        order.Cancel("Out of stock.", Now.AddMinutes(-30));

        var result = await Handler().HandleAsync(new RecordShipment(order.Id, "WH-1", Now), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Conflict);
    }

    [Fact]
    public async Task HandleAsync_ForAnUnknownOrder_IsNotFound()
    {
        _orders.GetForFulfilmentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);

        var result = await Handler().HandleAsync(new RecordShipment(Guid.NewGuid(), "WH-1", Now), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    // A warehouse clock or a mistyped date must not put the shipment before the order existed.
    [Fact]
    public async Task HandleAsync_ShippedBeforeTheOrderWasPlaced_IsRejected()
    {
        var order = Stored();

        var result = await Handler().HandleAsync(
            new RecordShipment(order.Id, "WH-1", PlacedAt.AddHours(-1)), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
        order.Status.Should().Be(OrderStatus.Placed);
    }

    [Fact]
    public void Validator_WithAShipmentTimeFarInTheFuture_Fails()
    {
        var result = new RecordShipmentValidator(_clock).TestValidate(
            new RecordShipment(Guid.NewGuid(), "WH-1", Now.AddMinutes(6)));

        result.ShouldHaveValidationErrorFor(c => c.ShippedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Validator_WithABlankShipmentId_Fails(string shipmentId)
    {
        var result = new RecordShipmentValidator(_clock).TestValidate(
            new RecordShipment(Guid.NewGuid(), shipmentId, Now));

        result.ShouldHaveValidationErrorFor(c => c.ShipmentId);
    }

    [Fact]
    public void Validator_WithAnEmptyOrderId_Fails()
    {
        var result = new RecordShipmentValidator(_clock).TestValidate(
            new RecordShipment(Guid.Empty, "WH-1", Now));

        result.ShouldHaveValidationErrorFor(c => c.OrderId);
    }
}
