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
        var head = Order.Place(Guid.NewGuid(), "SKU-1", 1, PlacedAt);
        _repository.ListAsync(Arg.Any<int>(), Arg.Any<(DateTimeOffset, Guid)?>(), Arg.Any<CancellationToken>())
            .Returns([
                head,
                Order.Place(Guid.NewGuid(), "SKU-2", 1, PlacedAt.AddMinutes(-1)),
            ]);
        var handler = new GetOrdersHandler(_repository);

        var first = await handler.HandleAsync(new GetOrders(1, null), CancellationToken.None);
        _repository.ClearReceivedCalls();
        await handler.HandleAsync(new GetOrders(1, first.Value.NextCursor), CancellationToken.None);

        // Asserting only "a cursor was passed" (a != null) would let a TryDecode that returns
        // the wrong position, or swaps the two fields, pass silently - it is the exact
        // (PlacedAt, Id) of the last row on page one that must reach the repository.
        await _repository.Received(1).ListAsync(
            2,
            Arg.Is<(DateTimeOffset PlacedAt, Guid Id)?>(a => a!.Value.PlacedAt == head.PlacedAt && a.Value.Id == head.Id),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The Encode/TryDecode pair has no coverage anywhere else: Application.Tests substitutes
    /// the repository and Infrastructure.Tests builds cursor tuples by hand, so nothing else
    /// round-trips a real cursor string. A format that silently drops sub-second precision
    /// (e.g. "u" instead of "O") would still look like "a cursor was passed" but decode to the
    /// wrong instant - this test fails loudly on that, driven only through the handler's public
    /// surface.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WithACursorFromASubSecondPlacedAt_RoundTripsTheExactInstant()
    {
        var precise = new DateTimeOffset(2026, 8, 31, 12, 0, 0, 123, TimeSpan.Zero).AddTicks(4567);
        var head = Order.Place(Guid.NewGuid(), "SKU-1", 1, precise);
        _repository.ListAsync(Arg.Any<int>(), Arg.Any<(DateTimeOffset, Guid)?>(), Arg.Any<CancellationToken>())
            .Returns([
                head,
                Order.Place(Guid.NewGuid(), "SKU-2", 1, precise.AddMinutes(-1)),
            ]);
        var handler = new GetOrdersHandler(_repository);

        var first = await handler.HandleAsync(new GetOrders(1, null), CancellationToken.None);
        _repository.ClearReceivedCalls();
        await handler.HandleAsync(new GetOrders(1, first.Value.NextCursor), CancellationToken.None);

        await _repository.Received(1).ListAsync(
            2,
            Arg.Is<(DateTimeOffset PlacedAt, Guid Id)?>(a => a!.Value.PlacedAt == precise && a.Value.Id == head.Id),
            Arg.Any<CancellationToken>());
    }
}
