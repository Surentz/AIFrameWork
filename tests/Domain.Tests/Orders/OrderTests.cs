using AiFramework.Domain.Orders;
using FluentAssertions;

namespace AiFramework.Domain.Tests.Orders;

public sealed class OrderTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Place_WithValidDetails_SetsTheProperties()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var order = Order.Place(id, userId, "SKU-1", 3, PlacedAt);

        order.Id.Should().Be(id);
        order.UserId.Should().Be(userId);
        order.Sku.Should().Be("SKU-1");
        order.Quantity.Should().Be(3);
        order.PlacedAt.Should().Be(PlacedAt);
    }

    [Fact]
    public void Place_WithNoUser_Throws()
    {
        var act = () => Order.Place(Guid.NewGuid(), Guid.Empty, "SKU-1", 1, PlacedAt);

        act.Should().Throw<DomainException>().WithMessage("*user*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Place_WithNonPositiveQuantity_Throws(int quantity)
    {
        var act = () => Order.Place(Guid.NewGuid(), Guid.NewGuid(), "SKU-1", quantity, PlacedAt);

        act.Should().Throw<DomainException>().WithMessage("*quantity*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Place_WithBlankSku_Throws(string sku)
    {
        var act = () => Order.Place(Guid.NewGuid(), Guid.NewGuid(), sku, 1, PlacedAt);

        act.Should().Throw<DomainException>().WithMessage("*sku*");
    }

    [Fact]
    public void Place_RaisesOrderPlaced()
    {
        var id = Guid.NewGuid();

        var order = Order.Place(id, Guid.NewGuid(), "SKU-1", 3, PlacedAt);

        order.DomainEvents.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new OrderPlaced(id, "SKU-1", 3));
    }
}
