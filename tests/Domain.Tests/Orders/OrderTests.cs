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
        var product = new OrderedProduct(Guid.NewGuid(), "Widget", 19.95m);

        var order = Order.Place(id, userId, 3, PlacedAt, product, "SKU-1");

        order.Id.Should().Be(id);
        order.UserId.Should().Be(userId);
        order.Sku.Should().Be("SKU-1");
        order.Quantity.Should().Be(3);
        order.PlacedAt.Should().Be(PlacedAt);
        order.Product.Should().Be(product);
    }

    [Fact]
    public void Place_WithNoProduct_Throws()
    {
        var act = () => Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1, PlacedAt, null!, "SKU-1");

        act.Should().Throw<DomainException>().WithMessage("*product*");
    }

    [Fact]
    public void Place_WithNoUser_Throws()
    {
        var act = () => Order.Place(
            Guid.NewGuid(), Guid.Empty, 1, PlacedAt, AnOrderedProduct.Any(), "SKU-1");

        act.Should().Throw<DomainException>().WithMessage("*user*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Place_WithNonPositiveQuantity_Throws(int quantity)
    {
        var act = () => Order.Place(
            Guid.NewGuid(), Guid.NewGuid(), quantity, PlacedAt, AnOrderedProduct.Any(), "SKU-1");

        act.Should().Throw<DomainException>().WithMessage("*quantity*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Place_WithBlankSku_Throws(string sku)
    {
        var act = () => Order.Place(
            Guid.NewGuid(), Guid.NewGuid(), 1, PlacedAt, AnOrderedProduct.Any(), sku);

        act.Should().Throw<DomainException>().WithMessage("*sku*");
    }

    [Fact]
    public void Place_RaisesOrderPlaced()
    {
        var id = Guid.NewGuid();

        var order = Order.Place(
            id, Guid.NewGuid(), 3, PlacedAt, AnOrderedProduct.Any(), "SKU-1");

        order.DomainEvents.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new OrderPlaced(id, "SKU-1", 3));
    }
}
