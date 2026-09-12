using AiFramework.Application.Abstractions;
using AiFramework.Application.Products;
using AiFramework.Domain.Products;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Products;

public sealed class UpdateProductHandlerTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid ProductId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public UpdateProductHandlerTests() => _clock.UtcNow.Returns(Now);

    private UpdateProductHandler Handler() => new(_repository, _clock);

    private Product Existing()
    {
        var product = Product.Create(ProductId, "SKU-1", "Widget", "A widget.", 9.99m, CreatedAt);
        _repository.GetForUpdateAsync(ProductId, Arg.Any<CancellationToken>()).Returns(product);
        return product;
    }

    [Fact]
    public async Task HandleAsync_WithAKnownProduct_AppliesTheChanges()
    {
        var product = Existing();

        var result = await Handler().HandleAsync(
            new UpdateProduct(ProductId, "Gadget", "A gadget.", 19.50m), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        product.Name.Should().Be("Gadget");
        product.Description.Should().Be("A gadget.");
        product.Price.Should().Be(19.50m);
        product.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task HandleAsync_LoadsTheProductForUpdate()
    {
        // A no-tracking read here would leave the mutation unsaved with no error, so the
        // handler must reach for the tracked overload specifically.
        Existing();

        await Handler().HandleAsync(
            new UpdateProduct(ProductId, "Gadget", null, 1m), CancellationToken.None);

        await _repository.Received(1).GetForUpdateAsync(ProductId, Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().GetAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LeavesTheSkuAlone()
    {
        var product = Existing();

        await Handler().HandleAsync(
            new UpdateProduct(ProductId, "Gadget", null, 1m), CancellationToken.None);

        product.Sku.Should().Be("SKU-1");
    }

    [Fact]
    public async Task HandleAsync_WithAnUnknownProduct_ReturnsNotFound()
    {
        var result = await Handler().HandleAsync(
            new UpdateProduct(Guid.NewGuid(), "Gadget", null, 1m), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.NotFound);
        result.Error.Code.Should().Be("product.not_found");
    }

    [Fact]
    public Task HandleAsync_WithNullCommand_Throws()
    {
        var act = () => Handler().HandleAsync(null!, CancellationToken.None);

        return act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void UpdateProduct_EvictsBothCatalogueQueries()
    {
        var tags = new UpdateProduct(ProductId, "Gadget", null, 1m).Tags;

        tags.Should().Equal(nameof(GetProducts), nameof(GetProduct));
    }
}
