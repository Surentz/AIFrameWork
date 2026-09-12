using AiFramework.Application.Abstractions;
using AiFramework.Application.Products;
using AiFramework.Domain.Products;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Products;

public sealed class GetProductsHandlerTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();

    private GetProductsHandler Handler() => new(_repository);

    /// <summary>Newest first, matching the order the repository returns rows in.</summary>
    private static Product NewProduct(int index) =>
        Product.Create(
            Guid.NewGuid(), $"SKU-{index}", $"Widget {index}", null, index,
            CreatedAt.AddMinutes(-index));

    private void Returns(params Product[] products) =>
        _repository.ListAsync(
                Arg.Any<int>(),
                Arg.Any<(DateTimeOffset CreatedAt, Guid Id)?>(),
                Arg.Any<CancellationToken>())
            .Returns(products);

    [Fact]
    public async Task HandleAsync_ProjectsTheRowsOntoListItems()
    {
        var product = NewProduct(1);
        Returns(product);

        var result = await Handler().HandleAsync(new GetProducts(20, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProductListItem(
                product.Id, product.Sku, product.Name, product.Price, product.CreatedAt));
    }

    [Fact]
    public async Task HandleAsync_AsksForOneMoreRowThanRequested()
    {
        // So "is there a next page" needs no second COUNT.
        Returns();

        await Handler().HandleAsync(new GetProducts(20, null), CancellationToken.None);

        await _repository.Received(1).ListAsync(
            21, Arg.Any<(DateTimeOffset CreatedAt, Guid Id)?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithNoExtraRow_ReturnsNoCursor()
    {
        Returns(NewProduct(1), NewProduct(2));

        var result = await Handler().HandleAsync(new GetProducts(2, null), CancellationToken.None);

        result.Value.Items.Should().HaveCount(2);
        result.Value.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WithAnExtraRow_TrimsItAndReturnsACursor()
    {
        Returns(NewProduct(1), NewProduct(2), NewProduct(3));

        var result = await Handler().HandleAsync(new GetProducts(2, null), CancellationToken.None);

        result.Value.Items.Should().HaveCount(2);
        result.Value.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAsync_ReturnsACursorPointingAtTheLastItemOnThePage()
    {
        var products = new[] { NewProduct(1), NewProduct(2), NewProduct(3) };
        Returns(products);

        var result = await Handler().HandleAsync(new GetProducts(2, null), CancellationToken.None);

        KeysetCursor.TryDecode(result.Value.NextCursor!, out var decoded).Should().BeTrue();
        decoded.Timestamp.Should().Be(products[1].CreatedAt);
        decoded.Id.Should().Be(products[1].Id);
    }

    [Fact]
    public async Task HandleAsync_WithACursor_PassesTheDecodedKeysetToTheRepository()
    {
        var product = NewProduct(1);
        Returns();

        await Handler().HandleAsync(
            new GetProducts(20, KeysetCursor.Encode(product.CreatedAt, product.Id)),
            CancellationToken.None);

        await _repository.Received(1).ListAsync(
            Arg.Any<int>(),
            Arg.Is<(DateTimeOffset CreatedAt, Guid Id)?>(a =>
                a!.Value.CreatedAt == product.CreatedAt && a.Value.Id == product.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithAMalformedCursor_ReturnsValidationAndReadsNothing()
    {
        // A 400, never a 500, and never a silent empty first page.
        var result = await Handler().HandleAsync(
            new GetProducts(20, "not-a-cursor"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("products.malformed_cursor");
        await _repository.DidNotReceive().ListAsync(
            Arg.Any<int>(),
            Arg.Any<(DateTimeOffset CreatedAt, Guid Id)?>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task HandleAsync_WithAnOutOfRangeLimit_ReturnsValidation(int limit)
    {
        var result = await Handler().HandleAsync(
            new GetProducts(limit, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("products.limit_out_of_range");
    }

    [Fact]
    public Task HandleAsync_WithNullQuery_Throws()
    {
        var act = () => Handler().HandleAsync(null!, CancellationToken.None);

        return act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void CacheKey_DistinguishesTheFirstPageFromAClientSuppliedCursor()
    {
        // "N" is the null case's WHOLE fragment and every non-null case is prefixed "C", so no
        // cursor a client can send collides with the first page's key.
        new GetProducts(20, null).CacheKey.Should().Be("20:N");
        new GetProducts(20, "N").CacheKey.Should().Be("20:CN");
        new GetProducts(20, "").CacheKey.Should().Be("20:C");
    }
}
