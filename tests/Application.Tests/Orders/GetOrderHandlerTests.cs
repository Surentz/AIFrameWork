using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

public sealed class GetOrderHandlerTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public GetOrderHandlerTests() => _currentUser.Id.Returns(UserId);

    [Fact]
    public async Task HandleAsync_WhenTheOrderExists_ReturnsTheView()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id, UserId, Arg.Any<CancellationToken>())
            .Returns(Order.Place(id, UserId, 4, PlacedAt, AnOrderedProduct.Any(), "SKU-1"));
        var handler = new GetOrderHandler(_repository, _currentUser);

        var result = await handler.HandleAsync(new GetOrder(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new OrderView(id, "SKU-1", 4, PlacedAt));
    }

    [Fact]
    public async Task HandleAsync_WhenTheOrderIsMissing_ReturnsNotFound()
    {
        _repository.GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);
        var handler = new GetOrderHandler(_repository, _currentUser);

        var result = await handler.HandleAsync(new GetOrder(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task HandleAsync_WhenQueried_AsksOnlyForTheCallersOwnOrder()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);
        var handler = new GetOrderHandler(_repository, _currentUser);

        await handler.HandleAsync(new GetOrder(id), CancellationToken.None);

        await _repository.Received(1).GetAsync(id, UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithNoCurrentUser_ReturnsUnauthorized()
    {
        _currentUser.Id.Returns((Guid?)null);
        var handler = new GetOrderHandler(_repository, _currentUser);

        var result = await handler.HandleAsync(new GetOrder(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }
}
