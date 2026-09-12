using AiFramework.Domain.Products;
using FluentAssertions;

namespace AiFramework.Domain.Tests.Products;

public sealed class ProductTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = new(2026, 9, 2, 9, 30, 0, TimeSpan.Zero);

    private static Product Create(
        string sku = "sku-1", string name = "Widget", string? description = "A widget.",
        decimal price = 9.99m) =>
        Product.Create(Guid.NewGuid(), sku, name, description, price, CreatedAt);

    [Fact]
    public void Create_WithValidDetails_SetsTheProperties()
    {
        var id = Guid.NewGuid();

        var product = Product.Create(id, "SKU-1", "Widget", "A widget.", 9.99m, CreatedAt);

        product.Id.Should().Be(id);
        product.Sku.Should().Be("SKU-1");
        product.Name.Should().Be("Widget");
        product.Description.Should().Be("A widget.");
        product.Price.Should().Be(9.99m);
        product.CreatedAt.Should().Be(CreatedAt);
    }

    [Fact]
    public void Create_SetsUpdatedAtToCreatedAt()
    {
        // Never null, so a caller displaying "last changed" needs no coalesce.
        Create().UpdatedAt.Should().Be(CreatedAt);
    }

    [Theory]
    [InlineData("sku-1", "SKU-1")]
    [InlineData("  sku-1  ", "SKU-1")]
    [InlineData("SkU-1", "SKU-1")]
    public void Create_NormalizesTheSku(string input, string expected)
    {
        Create(sku: input).Sku.Should().Be(expected);
    }

    [Fact]
    public void Create_TrimsTheName()
    {
        Create(name: "  Widget  ").Name.Should().Be("Widget");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithNoMeaningfulDescription_StoresNull(string? description)
    {
        // One representation of "absent", so no caller has to treat "   " as present-but-empty.
        Create(description: description).Description.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankSku_Throws(string sku)
    {
        var act = () => Create(sku: sku);

        act.Should().Throw<DomainException>().WithMessage("*sku*");
    }

    [Fact]
    public void Create_WithOverlongSku_Throws()
    {
        var act = () => Create(sku: new string('a', Product.MaxSkuLength + 1));

        act.Should().Throw<DomainException>().WithMessage("*sku*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_Throws(string name)
    {
        var act = () => Create(name: name);

        act.Should().Throw<DomainException>().WithMessage("*name*");
    }

    [Fact]
    public void Create_WithOverlongName_Throws()
    {
        var act = () => Create(name: new string('a', Product.MaxNameLength + 1));

        act.Should().Throw<DomainException>().WithMessage("*name*");
    }

    [Fact]
    public void Create_WithOverlongDescription_Throws()
    {
        var act = () => Create(description: new string('a', Product.MaxDescriptionLength + 1));

        act.Should().Throw<DomainException>().WithMessage("*description*");
    }

    [Fact]
    public void Create_WithNegativePrice_Throws()
    {
        var act = () => Create(price: -0.01m);

        act.Should().Throw<DomainException>().WithMessage("*negative*");
    }

    [Fact]
    public void Create_WithFreePrice_IsAllowed()
    {
        // Zero is a legitimate price; only negative is refused.
        Create(price: 0m).Price.Should().Be(0m);
    }

    [Fact]
    public void Create_AboveMaxPrice_Throws()
    {
        var act = () => Create(price: Product.MaxPrice + 0.01m);

        act.Should().Throw<DomainException>().WithMessage("*price*");
    }

    [Fact]
    public void Create_WithMoreDecimalPlacesThanTheColumnHolds_Throws()
    {
        // numeric(18,2) would round this away on write and the value read back would differ
        // from the one sent. Refused here rather than silently changed there.
        var act = () => Create(price: 1.005m);

        act.Should().Throw<DomainException>().WithMessage("*decimal places*");
    }

    [Fact]
    public void Update_ChangesTheEditableFields()
    {
        var product = Create();

        product.Update("Gadget", "A gadget.", 19.50m, UpdatedAt);

        product.Name.Should().Be("Gadget");
        product.Description.Should().Be("A gadget.");
        product.Price.Should().Be(19.50m);
        product.UpdatedAt.Should().Be(UpdatedAt);
    }

    [Fact]
    public void Update_LeavesTheSkuAlone()
    {
        // The sku is how everything outside the catalogue refers to this product, so there is
        // deliberately no way to change it. This test is the guard on that decision.
        var product = Create(sku: "SKU-1");

        product.Update("Gadget", null, 1m, UpdatedAt);

        product.Sku.Should().Be("SKU-1");
    }

    [Fact]
    public void Update_LeavesCreatedAtAlone()
    {
        var product = Create();

        product.Update("Gadget", null, 1m, UpdatedAt);

        product.CreatedAt.Should().Be(CreatedAt);
    }

    [Fact]
    public void Update_CanClearTheDescription()
    {
        var product = Create(description: "A widget.");

        product.Update("Widget", null, 9.99m, UpdatedAt);

        product.Description.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Update_WithBlankName_Throws(string name)
    {
        var product = Create();

        var act = () => product.Update(name, null, 1m, UpdatedAt);

        act.Should().Throw<DomainException>().WithMessage("*name*");
    }

    [Fact]
    public void Update_WithNegativePrice_Throws()
    {
        var product = Create();

        var act = () => product.Update("Widget", null, -1m, UpdatedAt);

        act.Should().Throw<DomainException>().WithMessage("*negative*");
    }

    [Fact]
    public void Update_WhenItThrows_LeavesTheProductUntouched()
    {
        // Every guard runs before the first assignment, so a rejected update is not a partial
        // one - the name must not be applied when the price that follows it is refused.
        var product = Create(name: "Widget", price: 9.99m);

        var act = () => product.Update("Gadget", null, -1m, UpdatedAt);

        act.Should().Throw<DomainException>();
        product.Name.Should().Be("Widget");
        product.Price.Should().Be(9.99m);
        product.UpdatedAt.Should().Be(CreatedAt);
    }

    [Fact]
    public void Create_RaisesNoDomainEvents()
    {
        // Unlike Order.Place. Recorded as a test because adding one later is not free: the
        // registration-completeness tests require every IDomainEvent in this assembly to have
        // both an AddDomainEvent<T> registration and a resolvable handler.
        Create().DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeSku_WithNull_Throws()
    {
        var act = () => Product.NormalizeSku(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
