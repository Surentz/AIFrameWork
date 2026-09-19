using AiFramework.Domain.Orders;
using FluentAssertions;

namespace AiFramework.Domain.Tests.Orders;

/// <summary>
/// The Placed -> Shipped / Placed -> Cancelled state machine. Kept beside OrderTests rather than
/// inside it because Place's own invariants and the transitions off it are separate concerns.
/// </summary>
public sealed class OrderLifecycleTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ShippedAt = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CancelledAt = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private static Order Placed(Guid? userId = null)
    {
        var order = Order.Place(
            Guid.NewGuid(), userId ?? Guid.NewGuid(), 2, PlacedAt, AnOrderedProduct.Any(), "SKU-1");

        // Place raises OrderPlaced; clearing keeps each test's assertion about the event IT
        // caused, rather than about the pair.
        order.ClearDomainEvents();
        return order;
    }

    [Fact]
    public void Place_StartsPlaced()
    {
        var order = Order.Place(
            Guid.NewGuid(), Guid.NewGuid(), 1, PlacedAt, AnOrderedProduct.Any(), "SKU-1");

        order.Status.Should().Be(OrderStatus.Placed);
        order.ShippedAt.Should().BeNull();
        order.CancelledAt.Should().BeNull();
        order.CancellationReason.Should().BeNull();
    }

    [Fact]
    public void Ship_FromPlaced_RecordsTheTransition()
    {
        var order = Placed();

        order.Ship(ShippedAt);

        order.Status.Should().Be(OrderStatus.Shipped);
        order.ShippedAt.Should().Be(ShippedAt);
    }

    [Fact]
    public void Ship_FromPlaced_RaisesOrderShipped()
    {
        var userId = Guid.NewGuid();
        var order = Placed(userId);

        order.Ship(ShippedAt);

        order.DomainEvents.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new OrderShipped(order.Id, userId, "SKU-1"));
    }

    [Fact]
    public void Ship_Twice_Throws()
    {
        // Shipping twice means two parcels — a broken invariant, not an imprecise caller.
        var order = Placed();
        order.Ship(ShippedAt);

        var act = () => order.Ship(ShippedAt.AddHours(1));

        act.Should().Throw<DomainException>().WithMessage("*already shipped*");
    }

    [Fact]
    public void Ship_AfterCancelling_Throws()
    {
        var order = Placed();
        order.Cancel("Out of stock.", CancelledAt);

        var act = () => order.Ship(ShippedAt);

        act.Should().Throw<DomainException>().WithMessage("*cancelled*cannot ship*");
    }

    [Fact]
    public void Cancel_FromPlaced_RecordsTheTransition()
    {
        var order = Placed();

        order.Cancel("Out of stock.", CancelledAt);

        order.Status.Should().Be(OrderStatus.Cancelled);
        order.CancelledAt.Should().Be(CancelledAt);
        order.CancellationReason.Should().Be("Out of stock.");
    }

    [Fact]
    public void Cancel_FromPlaced_RaisesOrderCancelled()
    {
        var userId = Guid.NewGuid();
        var order = Placed(userId);

        order.Cancel("Out of stock.", CancelledAt);

        order.DomainEvents.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new OrderCancelled(order.Id, userId, "SKU-1", "Out of stock."));
    }

    [Fact]
    public void Cancel_TrimsTheReason()
    {
        var order = Placed();

        order.Cancel("  Out of stock.  ", CancelledAt);

        order.CancellationReason.Should().Be("Out of stock.");
    }

    [Fact]
    public void Cancel_Twice_Throws()
    {
        var order = Placed();
        order.Cancel("Out of stock.", CancelledAt);

        var act = () => order.Cancel("Changed my mind.", CancelledAt.AddHours(1));

        act.Should().Throw<DomainException>().WithMessage("*already cancelled*");
    }

    [Fact]
    public void Cancel_AfterShipping_Throws()
    {
        // Once it is in the post, cancelling is a return — a different thing entirely.
        var order = Placed();
        order.Ship(ShippedAt);

        var act = () => order.Cancel("Changed my mind.", CancelledAt);

        act.Should().Throw<DomainException>().WithMessage("*shipped*cannot be cancelled*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Cancel_WithNoMeaningfulReason_Throws(string? reason)
    {
        var order = Placed();

        var act = () => order.Cancel(reason!, CancelledAt);

        act.Should().Throw<DomainException>().WithMessage("*reason*");
    }

    [Fact]
    public void Cancel_WithAReasonOverTheLimit_Throws()
    {
        var order = Placed();
        var reason = new string('a', Order.MaxCancellationReasonLength + 1);

        var act = () => order.Cancel(reason, CancelledAt);

        act.Should().Throw<DomainException>().WithMessage("*reason*longer*");
    }

    [Fact]
    public void Cancel_WhenItThrows_LeavesTheOrderUnchanged()
    {
        // The guards run before any state is written, so a refused cancellation cannot leave a
        // half-cancelled order behind.
        var order = Placed();

        var act = () => order.Cancel("   ", CancelledAt);

        act.Should().Throw<DomainException>();
        order.Status.Should().Be(OrderStatus.Placed);
        order.CancelledAt.Should().BeNull();
        order.CancellationReason.Should().BeNull();
        order.DomainEvents.Should().BeEmpty();
    }
}
