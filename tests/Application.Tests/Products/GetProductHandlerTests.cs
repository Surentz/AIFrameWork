using AiFramework.Application.Abstractions;
using AiFramework.Application.Products;
using AiFramework.Domain.Products;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Products;

public sealed class GetProductHandlerTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProductId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();

    [Fact]
    public async Task HandleAsync_WithAKnownProduct_ReturnsTheView()
    {
        _repository.GetAsync(ProductId, Arg.Any<CancellationToken>()).Returns(
            Product.Create(ProductId, "SKU-1", "Widget", "A widget.", 9.99m, CreatedAt));

        var result = await new GetProductHandler(_repository)
            .HandleAsync(new GetProduct(ProductId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new ProductView(
            ProductId, "SKU-1", "Widget", "A widget.", 9.99m, CreatedAt, CreatedAt));
    }

    [Fact]
    public async Task HandleAsync_WithAnUnknownProduct_ReturnsNotFound()
    {
        var result = await new GetProductHandler(_repository)
            .HandleAsync(new GetProduct(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.NotFound);
        result.Error.Code.Should().Be("product.not_found");
    }

    [Fact]
    public Task HandleAsync_WithNullQuery_Throws()
    {
        var act = () => new GetProductHandler(_repository)
            .HandleAsync(null!, CancellationToken.None);

        return act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void CacheKey_IsTheIdAlone()
    {
        // No user id: the caching behavior prepends the query type and the caller, and adding
        // one here would duplicate it rather than secure anything.
        new GetProduct(ProductId).CacheKey.Should().Be(ProductId.ToString());
    }
}
