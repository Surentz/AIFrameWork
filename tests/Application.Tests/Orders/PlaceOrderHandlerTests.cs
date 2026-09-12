using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Application.Products;
using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

public sealed class PlaceOrderHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly IProductRepository _products = Substitute.For<IProductRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public PlaceOrderHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _currentUser.Id.Returns(UserId);

        var product = Product.Create(Guid.NewGuid(), "SKU-1", "Widget", null, 19.95m, Now);
        _products.GetBySkuAsync("SKU-1", Arg.Any<CancellationToken>()).Returns(product);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_AddsTheOrder()
    {
        var handler = new PlaceOrderHandler(_repository, _products, _clock, _currentUser);

        var result = await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<Order>(o => o.Sku == "SKU-1" && o.Quantity == 2 && o.PlacedAt == Now),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_RecordsThePlacingUser()
    {
        var handler = new PlaceOrderHandler(_repository, _products, _clock, _currentUser);

        await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        await _repository.Received(1).AddAsync(
            Arg.Is<Order>(o => o.UserId == UserId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ReturnsTheNewOrderId()
    {
        var handler = new PlaceOrderHandler(_repository, _products, _clock, _currentUser);

        var result = await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        result.Value.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithNoCurrentUser_ReturnsUnauthorizedAndWritesNothing()
    {
        _currentUser.Id.Returns((Guid?)null);
        var handler = new PlaceOrderHandler(_repository, _products, _clock, _currentUser);

        var result = await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
        await _repository.DidNotReceive().AddAsync(
            Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithASkuNotInTheCatalogue_FailsValidation()
    {
        _products.GetBySkuAsync("SKU-MISSING", Arg.Any<CancellationToken>())
            .Returns((Product?)null);
        var handler = new PlaceOrderHandler(_repository, _products, _clock, _currentUser);

        var result = await handler.HandleAsync(
            new PlaceOrder("SKU-MISSING", 2), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("orders.unknown_sku");
    }

    [Fact]
    public async Task HandleAsync_LooksTheSkuUpInItsNormalizedForm()
    {
        // Product.Sku is stored upper-case, so a lower-case submission must still match.
        var handler = new PlaceOrderHandler(_repository, _products, _clock, _currentUser);

        var result = await handler.HandleAsync(
            new PlaceOrder("sku-1", 2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _products.Received(1).GetBySkuAsync("SKU-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_SnapshotsTheProductOntoTheOrder()
    {
        var product = await _products.GetBySkuAsync("SKU-1", CancellationToken.None);
        var handler = new PlaceOrderHandler(_repository, _products, _clock, _currentUser);

        await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        await _repository.Received(1).AddAsync(
            Arg.Is<Order>(o =>
                o.Sku == "SKU-1"
                && o.Product != null
                && product != null
                && o.Product.ProductId == product.Id
                && o.Product.Name == "Widget"
                && o.Product.UnitPrice == 19.95m),
            Arg.Any<CancellationToken>());
    }
}
