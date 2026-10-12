using AiFramework.Application.Abstractions;
using AiFramework.Application.Products;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Products;

public sealed class FindProductsHandlerTests
{
    private readonly IProductRepository _products = Substitute.For<IProductRepository>();

    private FindProductsHandler Handler => new(_products);

    [Fact]
    public async Task HandleAsync_PassesTheTextAndItsSkuShapedWords()
    {
        _products.FindAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ProductMatch>>([]));

        await Handler.HandleAsync(new FindProducts("price of sku-1042 or Widget", 5), CancellationToken.None);

        await _products.Received(1).FindAsync(
            "price of sku-1042 or Widget",
            Arg.Is<IReadOnlyList<string>>(skus => skus.Contains("SKU-1042") && skus.Contains("WIDGET")),
            FindProductsHandler.MinimumSimilarity,
            5,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("widget", 0)]
    [InlineData("widget", 11)]
    public async Task HandleAsync_RefusesBadInput(string text, int limit)
    {
        var result = await Handler.HandleAsync(new FindProducts(text, limit), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }
}
