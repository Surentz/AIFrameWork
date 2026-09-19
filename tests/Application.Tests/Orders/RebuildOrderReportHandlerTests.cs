using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

/// <summary>
/// The reference heavy job's handler. Its paging loop is the part worth testing here: the worker's
/// integration suite only ever sees a single empty page, so nothing else exercises a second
/// iteration or the failure branch.
/// </summary>
public sealed class RebuildOrderReportHandlerTests
{
    private readonly IQueryDispatcher _queries = Substitute.For<IQueryDispatcher>();
    private readonly IOrderReportWriter _writer = Substitute.For<IOrderReportWriter>();

    private RebuildOrderReportHandler Handler => new(_queries, _writer);

    private static OrderListItem Item(int quantity) =>
        new(
            Guid.NewGuid(),
            "SKU",
            quantity,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            null,
            OrderStatus.Placed);

    [Fact]
    public async Task Handle_WithOnePage_WritesItsTotals()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderPage>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderPage([Item(2), Item(3)], NextCursor: null)));

        var ownerId = Guid.NewGuid();
        await Handler.Handle(new RebuildOrderReport(ownerId), CancellationToken.None);

        await _writer.Received(1).WriteAsync(ownerId, 2, 5, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The loop itself: a non-null cursor must drive another query, and the totals must accumulate
    /// across pages rather than being overwritten by the last one.
    /// </summary>
    [Fact]
    public async Task Handle_WithSeveralPages_FollowsTheCursorAndAccumulates()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderPage>>(), Arg.Any<CancellationToken>())
            .Returns(
                Result.Success(new OrderPage([Item(1), Item(2)], NextCursor: "page-2")),
                Result.Success(new OrderPage([Item(4)], NextCursor: "page-3")),
                Result.Success(new OrderPage([Item(10)], NextCursor: null)));

        var ownerId = Guid.NewGuid();
        await Handler.Handle(new RebuildOrderReport(ownerId), CancellationToken.None);

        await _queries.Received(3)
            .SendAsync(Arg.Any<IQuery<OrderPage>>(), Arg.Any<CancellationToken>());
        await _writer.Received(1).WriteAsync(ownerId, 4, 17, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithNoOrders_WritesZeroes()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderPage>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderPage([], NextCursor: null)));

        var ownerId = Guid.NewGuid();
        await Handler.Handle(new RebuildOrderReport(ownerId), CancellationToken.None);

        await _writer.Received(1).WriteAsync(ownerId, 0, 0, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A failed <c>Result</c> must THROW, not return quietly. Wolverine's error policy decides
    /// retry versus dead-letter and can only see an exception; a handler that swallows the failure
    /// looks identical to one that succeeded, in every log and every metric.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAPageFails_ThrowsAndWritesNothing()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderPage>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<OrderPage>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid.")));

        var act = async () =>
            await Handler.Handle(new RebuildOrderReport(Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*auth.failed*", "the message must name why, so a dead letter is readable");

        await _writer.DidNotReceive().WriteAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The unauthorized case is the one that proves the job depends on <c>JobUserMiddleware</c>:
    /// with no current user, <c>GetOrders</c> fails this way and the job cannot succeed. The
    /// worker's integration test asserts the other half — that the middleware supplies one.
    /// </summary>
    [Fact]
    public async Task Handle_WhenTheSecondPageFails_StillThrows()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderPage>>(), Arg.Any<CancellationToken>())
            .Returns(
                Result.Success(new OrderPage([Item(1)], NextCursor: "page-2")),
                Result.Failure<OrderPage>(new Error(
                    ErrorKind.Validation, "orders.malformed_cursor", "The cursor could not be parsed.")));

        var act = async () =>
            await Handler.Handle(new RebuildOrderReport(Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*orders.malformed_cursor*");

        await _writer.DidNotReceive().WriteAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
