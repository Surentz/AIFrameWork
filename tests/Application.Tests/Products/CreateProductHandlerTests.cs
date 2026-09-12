using AiFramework.Application.Abstractions;
using AiFramework.Application.Products;
using AiFramework.Domain.Products;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Products;

public sealed class CreateProductHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public CreateProductHandlerTests() => _clock.UtcNow.Returns(Now);

    private CreateProductHandler Handler() => new(_repository, _clock);

    [Fact]
    public async Task HandleAsync_WithValidCommand_AddsTheProduct()
    {
        var result = await Handler().HandleAsync(
            new CreateProduct("SKU-1", "Widget", "A widget.", 9.99m), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<Product>(p => p.Sku == "SKU-1"
                && p.Name == "Widget"
                && p.Description == "A widget."
                && p.Price == 9.99m
                && p.CreatedAt == Now),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ReturnsTheNewProductId()
    {
        var result = await Handler().HandleAsync(
            new CreateProduct("SKU-1", "Widget", null, 1m), CancellationToken.None);

        result.Value.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ChecksAvailabilityAgainstTheNormalizedSku()
    {
        // The repository is keyed on the normalized form, so a lower-case sku must still find
        // an existing upper-case one - otherwise the check passes and the unique index turns a
        // friendly 409 into a 500 at SaveChanges.
        await Handler().HandleAsync(
            new CreateProduct(" sku-1 ", "Widget", null, 1m), CancellationToken.None);

        await _repository.Received(1).GetBySkuAsync("SKU-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithATakenSku_ReturnsConflictAndWritesNothing()
    {
        _repository.GetBySkuAsync("SKU-1", Arg.Any<CancellationToken>())
            .Returns(Product.Create(Guid.NewGuid(), "SKU-1", "Existing", null, 1m, Now));

        var result = await Handler().HandleAsync(
            new CreateProduct("sku-1", "Widget", null, 9.99m), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Conflict);
        result.Error.Code.Should().Be("product.sku_taken");
        await _repository.DidNotReceive().AddAsync(
            Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public Task HandleAsync_WithNullCommand_Throws()
    {
        var act = () => Handler().HandleAsync(null!, CancellationToken.None);

        return act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void CreateProduct_EvictsBothCatalogueQueries()
    {
        var tags = new CreateProduct("SKU-1", "Widget", null, 1m).Tags;

        tags.Should().Equal(nameof(GetProducts), nameof(GetProduct));
    }
}
