using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

/// <summary>
/// The two transition commands. The assertion that matters most in both: an illegal transition
/// becomes a 409 Conflict, not the 400 a bare DomainException would produce.
/// </summary>
public sealed class AdvanceOrderHandlerTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public AdvanceOrderHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _currentUser.Id.Returns(UserId);
    }

    private static Order AnOrder() =>
        Order.Place(Guid.NewGuid(), UserId, 2, PlacedAt, AnOrderedProduct.Any(), "SKU-1");

    private Order Stored()
    {
        var order = AnOrder();
        _orders.GetForUpdateAsync(order.Id, UserId, Arg.Any<CancellationToken>()).Returns(order);
        return order;
    }

    private ShipOrderHandler ShipHandler() => new(_orders, _currentUser, _clock);

    private CancelOrderHandler CancelHandler() => new(_orders, _currentUser, _clock);

    [Fact]
    public async Task ShipOrder_OnAPlacedOrder_ShipsIt()
    {
        var order = Stored();

        var result = await ShipHandler().HandleAsync(new ShipOrder(order.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Shipped);
        order.ShippedAt.Should().Be(Now);
    }

    [Fact]
    public async Task ShipOrder_ReadsTheOrderTracked()
    {
        // GetAsync reads untracked, so using it here would drop the write silently. This test is
        // the net under that.
        var order = Stored();

        await ShipHandler().HandleAsync(new ShipOrder(order.Id), CancellationToken.None);

        await _orders.Received(1).GetForUpdateAsync(
            order.Id, UserId, Arg.Any<CancellationToken>());
        await _orders.DidNotReceive().GetAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShipOrder_OnAnAlreadyShippedOrder_ReturnsConflict()
    {
        var order = Stored();
        order.Ship(Now.AddHours(-1));

        var result = await ShipHandler().HandleAsync(new ShipOrder(order.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Conflict);
    }

    [Fact]
    public async Task ShipOrder_OnACancelledOrder_ReturnsConflict()
    {
        var order = Stored();
        order.Cancel("Out of stock.", Now.AddHours(-1));

        var result = await ShipHandler().HandleAsync(new ShipOrder(order.Id), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Conflict);
    }

    [Fact]
    public async Task ShipOrder_WithAnIdThatDoesNotExist_ReturnsNotFound()
    {
        _orders.GetForUpdateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var result = await ShipHandler().HandleAsync(
            new ShipOrder(Guid.NewGuid()), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task ShipOrder_WithNoSession_ReturnsUnauthorized()
    {
        _currentUser.Id.Returns((Guid?)null);

        var result = await ShipHandler().HandleAsync(
            new ShipOrder(Guid.NewGuid()), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }

    [Fact]
    public async Task CancelOrder_OnAPlacedOrder_CancelsItWithTheReason()
    {
        var order = Stored();

        var result = await CancelHandler().HandleAsync(
            new CancelOrder(order.Id, "Out of stock."), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        order.CancellationReason.Should().Be("Out of stock.");
    }

    [Fact]
    public async Task CancelOrder_OnAShippedOrder_ReturnsConflict()
    {
        var order = Stored();
        order.Ship(Now.AddHours(-1));

        var result = await CancelHandler().HandleAsync(
            new CancelOrder(order.Id, "Changed my mind."), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Conflict);
    }

    [Fact]
    public async Task CancelOrder_ReportsTheNewStatus()
    {
        var order = Stored();

        var result = await CancelHandler().HandleAsync(
            new CancelOrder(order.Id, "Out of stock."), CancellationToken.None);

        result.Value.OrderId.Should().Be(order.Id);
        result.Value.Status.Should().Be(OrderStatus.Cancelled);
        result.Value.ChangedAt.Should().Be(Now);
    }

    [Fact]
    public async Task CancelOrder_WithAnIdThatDoesNotExist_ReturnsNotFound()
    {
        _orders.GetForUpdateAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        var result = await CancelHandler().HandleAsync(
            new CancelOrder(Guid.NewGuid(), "Out of stock."), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }
}
