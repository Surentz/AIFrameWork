using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

public sealed class GetOrderHandlerTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();

    [Fact]
    public async Task HandleAsync_WhenTheOrderExists_ReturnsTheView()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Order.Place(id, "SKU-1", 4, PlacedAt));
        var handler = new GetOrderHandler(_repository);

        var result = await handler.HandleAsync(new GetOrder(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new OrderView(id, "SKU-1", 4, PlacedAt));
    }

    [Fact]
    public async Task HandleAsync_WhenTheOrderIsMissing_ReturnsNotFound()
    {
        _repository.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);
        var handler = new GetOrderHandler(_repository);

        var result = await handler.HandleAsync(new GetOrder(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }
}
