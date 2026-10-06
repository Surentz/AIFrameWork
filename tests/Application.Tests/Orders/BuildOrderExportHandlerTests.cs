using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Application.Users;
using AiFramework.Domain.Orders;
using AiFramework.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

/// <summary>
/// The heavy job behind an export. Its paging loop, its early exit and its failure branch are the
/// parts worth testing here: the worker's integration suite sees one small happy path.
/// </summary>
public sealed class BuildOrderExportHandlerTests
{
    private static readonly Guid ExportId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly IQueryDispatcher _queries = Substitute.For<IQueryDispatcher>();
    private static readonly byte[] Pdf = "%PDF-1.7"u8.ToArray();

    private readonly ICommandDispatcher _commands = Substitute.For<ICommandDispatcher>();
    private readonly IOrderExportRenderer _renderer = Substitute.For<IOrderExportRenderer>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public BuildOrderExportHandlerTests()
    {
        ExportIs(OrderExportState.Requested);
        _commands.SendAsync(Arg.Any<ICommand<bool>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(true));
        _queries.SendAsync(Arg.Any<IQuery<SessionView>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionView(OwnerId, "jane", "Jane Doe", "stamp", UserRole.Member)));
        _renderer.Render(Arg.Any<OrderExportReport>()).Returns(Pdf);
        _clock.UtcNow.Returns(Now);
    }

    private BuildOrderExportHandler Handler => new(_queries, _commands, _renderer, _clock);

    private void ExportIs(OrderExportState state) =>
        _queries.SendAsync(Arg.Any<IQuery<OrderExportView>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderExportView(ExportId, state, Now, null, null)));

    private static OrderExportRow Row(string sku) =>
        new(Guid.NewGuid(), sku, "Widget", 1, 1m, OrderStatus.Placed, Now, null, null, null);

    [Fact]
    public async Task Handle_FollowsTheCursorAndCompletesWithEveryRow()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderExportRowPage>>(), Arg.Any<CancellationToken>())
            .Returns(
                Result.Success(new OrderExportRowPage([Row("A"), Row("B")], NextCursor: "page-2")),
                Result.Success(new OrderExportRowPage([Row("C")], NextCursor: null)));

        await Handler.Handle(new BuildOrderExport(ExportId, OwnerId), CancellationToken.None);

        await _queries.Received(1).SendAsync(
            Arg.Is<GetOrderExportRows>(q => q.Cursor == "page-2"),
            Arg.Any<CancellationToken>());
        _renderer.Received(1).Render(Arg.Is<OrderExportReport>(r => r.OrderCount == 3));
        await _commands.Received(1).SendAsync(
            Arg.Is<CompleteOrderExport>(c => c.ExportId == ExportId && c.RowCount == 3 && c.Document == Pdf),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NamesTheOwnerAndTheTimeInTheReport()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderExportRowPage>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderExportRowPage([], NextCursor: null)));

        await Handler.Handle(new BuildOrderExport(ExportId, OwnerId), CancellationToken.None);

        await _queries.Received(1).SendAsync(Arg.Is<GetUser>(q => q.Id == OwnerId), Arg.Any<CancellationToken>());
        _renderer.Received(1).Render(Arg.Is<OrderExportReport>(r =>
            r.OwnerName == "Jane Doe" && r.Generated == "Generated 3 Oct 2026, 12:00 UTC"));
    }

    [Fact]
    public Task Handle_WhenTheOwnerCannotBeRead_Throws()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderExportRowPage>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderExportRowPage([], NextCursor: null)));
        _queries.SendAsync(Arg.Any<IQuery<SessionView>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SessionView>(new Error(ErrorKind.NotFound, "user.not_found", "gone")));

        var act = () => Handler.Handle(new BuildOrderExport(ExportId, OwnerId), CancellationToken.None);

        return act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Handle_AsksForTheLargestPageTheQueryTakes()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderExportRowPage>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderExportRowPage([], NextCursor: null)));

        await Handler.Handle(new BuildOrderExport(ExportId, OwnerId), CancellationToken.None);

        await _queries.Received(1).SendAsync(
            Arg.Is<GetOrderExportRows>(q => q.Limit == GetOrderExportRows.MaxLimit && q.Cursor == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTheExportIsAlreadyReady_BuildsNothing()
    {
        ExportIs(OrderExportState.Ready);

        await Handler.Handle(new BuildOrderExport(ExportId, OwnerId), CancellationToken.None);

        await _queries.DidNotReceive().SendAsync(
            Arg.Any<IQuery<OrderExportRowPage>>(), Arg.Any<CancellationToken>());
        await _commands.DidNotReceive().SendAsync(Arg.Any<ICommand<bool>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTheExportIsGone_BuildsNothing()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderExportView>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<OrderExportView>(new Error(ErrorKind.NotFound, "order_exports.not_found", "gone")));

        await Handler.Handle(new BuildOrderExport(ExportId, OwnerId), CancellationToken.None);

        await _commands.DidNotReceive().SendAsync(Arg.Any<ICommand<bool>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public Task Handle_WhenReadingTheExportFailsForAnyOtherReason_Throws()
    {
        // Unauthorized is what a missing caller produces — the worker failing to set ICurrentUser
        // from OwnerId. Returning quietly here would hide exactly that bug.
        _queries.SendAsync(Arg.Any<IQuery<OrderExportView>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<OrderExportView>(new Error(ErrorKind.Unauthorized, "auth.failed", "no caller")));

        var act = () => Handler.Handle(new BuildOrderExport(ExportId, OwnerId), CancellationToken.None);

        return act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public Task Handle_WhenAPageFails_Throws()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderExportRowPage>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<OrderExportRowPage>(new Error(ErrorKind.Unavailable, "db.down", "down")));

        var act = () => Handler.Handle(new BuildOrderExport(ExportId, OwnerId), CancellationToken.None);

        return act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public Task Handle_WhenCompletingFails_Throws()
    {
        _queries.SendAsync(Arg.Any<IQuery<OrderExportRowPage>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderExportRowPage([], NextCursor: null)));
        _commands.SendAsync(Arg.Any<ICommand<bool>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<bool>(new Error(ErrorKind.Unavailable, "db.down", "down")));

        var act = () => Handler.Handle(new BuildOrderExport(ExportId, OwnerId), CancellationToken.None);

        return act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void BuildOrderExport_RunsOnTheHeavyLane()
    {
        BuildOrderExport.Lane.Should().Be(JobLane.Heavy);
    }
}
