using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

public sealed class GetOrdersHandlerTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();

    [Fact]
    public async Task HandleAsync_WithFewerRowsThanTheLimit_ReturnsNoNextCursor()
    {
        _repository.ListAsync(Arg.Any<int>(), Arg.Any<(DateTimeOffset, Guid)?>(), Arg.Any<CancellationToken>())
            .Returns([Order.Place(Guid.NewGuid(), "SKU-1", 1, PlacedAt)]);
        var handler = new GetOrdersHandler(_repository);

        var result = await handler.HandleAsync(new GetOrders(20, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WithMoreRowsThanTheLimit_TrimsToTheLimit()
    {
        var orders = Enumerable.Range(0, 3)
            .Select(i => Order.Place(Guid.NewGuid(), $"SKU-{i}", 1, PlacedAt.AddMinutes(-i)))
            .ToArray();
        _repository.ListAsync(Arg.Any<int>(), Arg.Any<(DateTimeOffset, Guid)?>(), Arg.Any<CancellationToken>())
            .Returns(orders);
        var handler = new GetOrdersHandler(_repository);

        var result = await handler.HandleAsync(new GetOrders(2, null), CancellationToken.None);

        result.Value.Items.Should().HaveCount(2);
        result.Value.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAsync_AsksTheRepositoryForOneMoreRowThanTheLimit()
    {
        _repository.ListAsync(Arg.Any<int>(), Arg.Any<(DateTimeOffset, Guid)?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var handler = new GetOrdersHandler(_repository);

        await handler.HandleAsync(new GetOrders(20, null), CancellationToken.None);

        await _repository.Received(1).ListAsync(21, null, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task HandleAsync_WithAnOutOfRangeLimit_ReturnsValidationFailure(int limit)
    {
        var handler = new GetOrdersHandler(_repository);

        var result = await handler.HandleAsync(new GetOrders(limit, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task HandleAsync_WithAMalformedCursor_ReturnsValidationFailureNotAnException()
    {
        var handler = new GetOrdersHandler(_repository);

        var result = await handler.HandleAsync(
            new GetOrders(20, "not-base64-at-all!!"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task HandleAsync_WithACursor_PassesTheDecodedPositionToTheRepository()
    {
        // Two rows, not one: the handler asks for limit+1 and infers "there is a next page"
        // only when MORE than the limit comes back. A single fixed row (as originally drafted)
        // makes the first HandleAsync(limit: 1) call receive exactly 1 row for a limit of 1 -
        // correctly no next page - so first.Value.NextCursor is null and the second assertion
        // below can never see a non-null cursor. Two rows make "there is a next page" true.
        _repository.ListAsync(Arg.Any<int>(), Arg.Any<(DateTimeOffset, Guid)?>(), Arg.Any<CancellationToken>())
            .Returns([
                Order.Place(Guid.NewGuid(), "SKU-1", 1, PlacedAt),
                Order.Place(Guid.NewGuid(), "SKU-2", 1, PlacedAt.AddMinutes(-1)),
            ]);
        var handler = new GetOrdersHandler(_repository);

        var first = await handler.HandleAsync(new GetOrders(1, null), CancellationToken.None);
        _repository.ClearReceivedCalls();
        await handler.HandleAsync(new GetOrders(1, first.Value.NextCursor), CancellationToken.None);

        await _repository.Received(1).ListAsync(2, Arg.Is<(DateTimeOffset, Guid)?>(a => a != null), Arg.Any<CancellationToken>());
    }
}
