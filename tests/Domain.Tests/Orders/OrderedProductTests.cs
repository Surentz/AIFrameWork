using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;
using FluentAssertions;

namespace AiFramework.Domain.Tests.Orders;

public sealed class OrderedProductTests
{
    [Fact]
    public void Constructor_WithValidDetails_SetsTheProperties()
    {
        var productId = Guid.NewGuid();

        var snapshot = new OrderedProduct(productId, "Widget", 19.95m);

        snapshot.ProductId.Should().Be(productId);
        snapshot.Name.Should().Be("Widget");
        snapshot.UnitPrice.Should().Be(19.95m);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithNoName_Throws(string name)
    {
        var act = () => new OrderedProduct(Guid.NewGuid(), name, 1m);

        act.Should().Throw<DomainException>().WithMessage("*name*");
    }

    [Fact]
    public void Constructor_WithANameOverTheMaximum_Throws()
    {
        var act = () => new OrderedProduct(
            Guid.NewGuid(), new string('x', Product.MaxNameLength + 1), 1m);

        act.Should().Throw<DomainException>().WithMessage("*name*");
    }

    [Fact]
    public void Constructor_WithANegativePrice_Throws()
    {
        var act = () => new OrderedProduct(Guid.NewGuid(), "Widget", -0.01m);

        act.Should().Throw<DomainException>().WithMessage("*price*");
    }

    [Fact]
    public void Constructor_WithMoreDecimalsThanTheScale_Throws()
    {
        // The column is numeric(18,2); a third decimal would be rounded away on write and the
        // snapshot would no longer equal what was charged.
        var act = () => new OrderedProduct(Guid.NewGuid(), "Widget", 1.005m);

        act.Should().Throw<DomainException>().WithMessage("*decimal places*");
    }

    [Fact]
    public void Constructor_WithNoProduct_Throws()
    {
        var act = () => new OrderedProduct(Guid.Empty, "Widget", 1m);

        act.Should().Throw<DomainException>().WithMessage("*product*");
    }

    [Fact]
    public void TwoSnapshotsWithTheSameValues_AreEqual()
    {
        // A record, because the snapshot has no identity of its own.
        var id = Guid.NewGuid();

        new OrderedProduct(id, "Widget", 1m).Should().Be(new OrderedProduct(id, "Widget", 1m));
    }
}
