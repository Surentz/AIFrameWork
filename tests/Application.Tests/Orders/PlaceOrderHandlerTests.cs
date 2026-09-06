using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

public sealed class PlaceOrderHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public PlaceOrderHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _currentUser.Id.Returns(UserId);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_AddsTheOrder()
    {
        var handler = new PlaceOrderHandler(_repository, _clock, _currentUser);

        var result = await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<Order>(o => o.Sku == "SKU-1" && o.Quantity == 2 && o.PlacedAt == Now),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_RecordsThePlacingUser()
    {
        var handler = new PlaceOrderHandler(_repository, _clock, _currentUser);

        await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        await _repository.Received(1).AddAsync(
            Arg.Is<Order>(o => o.UserId == UserId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ReturnsTheNewOrderId()
    {
        var handler = new PlaceOrderHandler(_repository, _clock, _currentUser);

        var result = await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        result.Value.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithNoCurrentUser_ReturnsUnauthorizedAndWritesNothing()
    {
        _currentUser.Id.Returns((Guid?)null);
        var handler = new PlaceOrderHandler(_repository, _clock, _currentUser);

        var result = await handler.HandleAsync(new PlaceOrder("SKU-1", 2), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
        await _repository.DidNotReceive().AddAsync(
            Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }
}
