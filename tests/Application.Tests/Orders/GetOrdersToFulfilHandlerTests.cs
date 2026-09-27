using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

public sealed class GetOrdersToFulfilHandlerTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();

    private static FulfilmentQueueRow ARow(int minute) => new(
        Guid.NewGuid(), Guid.NewGuid(), "buyer", "SKU-1", 1, PlacedAt.AddMinutes(minute),
        "Widget", 10m, OrderStatus.Placed);

    private void Returns(params FulfilmentQueueRow[] rows) =>
        _repository.ListForFulfilmentAsync(
                Arg.Any<OrderStatus>(), Arg.Any<int>(),
                Arg.Any<(DateTimeOffset, Guid)?>(), Arg.Any<CancellationToken>())
            .Returns(rows);

    private GetOrdersToFulfilHandler Handler() => new(_repository);

    [Fact]
    public async Task HandleAsync_WithFewerRowsThanTheLimit_ReturnsNoNextCursor()
    {
        Returns(ARow(0));

        var result = await Handler().HandleAsync(
            new GetOrdersToFulfil(OrderStatus.Placed, 20, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WithMoreRowsThanTheLimit_CursorsFromTheLastRowShown()
    {
        var rows = new[] { ARow(0), ARow(1), ARow(2) };
        Returns(rows);

        var result = await Handler().HandleAsync(
            new GetOrdersToFulfil(OrderStatus.Placed, 2, null), CancellationToken.None);

        result.Value.Items.Select(i => i.Id).Should().Equal(rows[0].Id, rows[1].Id);
        KeysetCursor.TryDecode(result.Value.NextCursor!, out var cursor).Should().BeTrue();
        cursor.Should().Be((rows[1].PlacedAt, rows[1].Id));
    }

    [Fact]
    public async Task HandleAsync_AsksForOneMoreRowThanTheLimit_InTheRequestedStatus()
    {
        Returns();

        await Handler().HandleAsync(
            new GetOrdersToFulfil(OrderStatus.Shipped, 20, null), CancellationToken.None);

        await _repository.Received(1).ListForFulfilmentAsync(
            OrderStatus.Shipped, 21, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithACursor_PassesItDecoded()
    {
        var id = Guid.NewGuid();
        Returns();

        await Handler().HandleAsync(
            new GetOrdersToFulfil(OrderStatus.Placed, 20, KeysetCursor.Encode(PlacedAt, id)),
            CancellationToken.None);

        await _repository.Received(1).ListForFulfilmentAsync(
            OrderStatus.Placed, 21, (PlacedAt, id), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task HandleAsync_WithALimitOutOfRange_ReturnsValidation(int limit)
    {
        var result = await Handler().HandleAsync(
            new GetOrdersToFulfil(OrderStatus.Placed, limit, null), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task HandleAsync_WithAMalformedCursor_ReturnsValidation()
    {
        var result = await Handler().HandleAsync(
            new GetOrdersToFulfil(OrderStatus.Placed, 20, "not a cursor"), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task HandleAsync_WithAnUndefinedStatus_ReturnsValidation()
    {
        var result = await Handler().HandleAsync(
            new GetOrdersToFulfil((OrderStatus)99, 20, null), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public void GetOrdersToFulfil_IsNotCacheable()
    {
        // An operations view must be current: a cached queue shows an order another operator has
        // just shipped as still waiting. ADR 0024.
        typeof(ICacheable).IsAssignableFrom(typeof(GetOrdersToFulfil)).Should().BeFalse();
    }
}
