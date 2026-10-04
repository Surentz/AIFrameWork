using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

/// <summary>
/// The export's commands and queries, ports substituted. Every one is the caller's own: ADR 0007
/// puts ownership in the repository signatures, and these tests check the caller is the one passed.
/// </summary>
public sealed class OrderExportHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IOrderExportRepository _exports = Substitute.For<IOrderExportRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public OrderExportHandlerTests()
    {
        _currentUser.Id.Returns(UserId);
        _clock.UtcNow.Returns(Now);
    }

    private static OrderExport Requested(DateTimeOffset? at = null)
    {
        var export = OrderExport.Request(Guid.NewGuid(), UserId, at ?? Now);
        export.ClearDomainEvents();
        return export;
    }

    [Fact]
    public async Task RequestOrderExport_WithNoExportInProgress_AddsANewOne()
    {
        var result = await new RequestOrderExportHandler(_exports, _currentUser, _clock)
            .HandleAsync(new RequestOrderExport(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(OrderExportState.Requested);
        result.Value.RequestedAt.Should().Be(Now);
        await _exports.Received(1).AddAsync(
            Arg.Is<OrderExport>(e => e.UserId == UserId && e.Id == result.Value.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestOrderExport_WithAnExportInProgress_ReturnsItInsteadOfAddingAnother()
    {
        var existing = Requested(Now.AddMinutes(-3));
        _exports.GetInProgressAsync(UserId, Now, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await new RequestOrderExportHandler(_exports, _currentUser, _clock)
            .HandleAsync(new RequestOrderExport(), CancellationToken.None);

        result.Value.Id.Should().Be(existing.Id);
        await _exports.DidNotReceive().AddAsync(Arg.Any<OrderExport>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestOrderExport_WithNoCaller_IsUnauthorized()
    {
        _currentUser.Id.Returns((Guid?)null);

        var result = await new RequestOrderExportHandler(_exports, _currentUser, _clock)
            .HandleAsync(new RequestOrderExport(), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }

    [Fact]
    public async Task CompleteOrderExport_CompletesTheCallersExport()
    {
        var export = Requested();
        _exports.GetForUpdateAsync(export.Id, UserId, Arg.Any<CancellationToken>()).Returns(export);

        var result = await new CompleteOrderExportHandler(_exports, _currentUser, _clock)
            .HandleAsync(new CompleteOrderExport(export.Id, "csv", 4), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        export.Status.Should().Be(OrderExportStatus.Ready);
        export.Content.Should().Be("csv");
        export.RowCount.Should().Be(4);
        export.CompletedAt.Should().Be(Now);
    }

    [Fact]
    public async Task CompleteOrderExport_ForAnExportTheCallerDoesNotHold_IsNotFound()
    {
        var result = await new CompleteOrderExportHandler(_exports, _currentUser, _clock)
            .HandleAsync(new CompleteOrderExport(Guid.NewGuid(), "csv", 0), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public void CompleteOrderExportValidator_RejectsANegativeRowCount()
    {
        var result = new CompleteOrderExportValidator().Validate(new CompleteOrderExport(Guid.NewGuid(), "csv", -1));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task GetOrderExports_ReadsAStaleRequestAsFailed()
    {
        var stale = new OrderExportSummary(
            Guid.NewGuid(), OrderExportStatus.Requested, Now - OrderExport.StaleAfter, null, null);
        var fresh = new OrderExportSummary(
            Guid.NewGuid(), OrderExportStatus.Requested, Now.AddMinutes(-1), null, null);
        var ready = new OrderExportSummary(
            Guid.NewGuid(), OrderExportStatus.Ready, Now.AddDays(-2), Now.AddDays(-2), 3);
        _exports.ListAsync(UserId, Arg.Any<CancellationToken>()).Returns([fresh, stale, ready]);

        var result = await new GetOrderExportsHandler(_exports, _currentUser, _clock)
            .HandleAsync(new GetOrderExports(), CancellationToken.None);

        result.Value.Select(v => v.Status).Should().Equal(
            OrderExportState.Requested, OrderExportState.Failed, OrderExportState.Ready);
    }

    [Fact]
    public async Task GetOrderExport_ForAnExportTheCallerDoesNotHold_IsNotFound()
    {
        var result = await new GetOrderExportHandler(_exports, _currentUser, _clock)
            .HandleAsync(new GetOrderExport(Guid.NewGuid()), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task GetOrderExportFile_NamesTheFileByTheRequestDateInUtc()
    {
        var id = Guid.NewGuid();
        var requestedAt = new DateTimeOffset(2026, 10, 4, 0, 30, 0, TimeSpan.FromHours(2));
        _exports.GetFileAsync(id, UserId, Arg.Any<CancellationToken>())
            .Returns(new OrderExportFile("csv", requestedAt));

        var result = await new GetOrderExportFileHandler(_exports, _currentUser)
            .HandleAsync(new GetOrderExportFile(id), CancellationToken.None);

        result.Value.Should().Be(new OrderExportDownload("orders-2026-10-03.csv", "csv"));
    }

    [Fact]
    public async Task GetOrderExportFile_WhenThereIsNoFile_IsNotFound()
    {
        var result = await new GetOrderExportFileHandler(_exports, _currentUser)
            .HandleAsync(new GetOrderExportFile(Guid.NewGuid()), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task GetOrderExportRows_MapsEveryColumnTheCsvNeeds()
    {
        var order = Order.Place(Guid.NewGuid(), UserId, 2, Now, AnOrderedProduct.Any(), "SKU-1");
        order.Cancel("Changed my mind.", Now.AddHours(1));
        _orders.ListAsync(UserId, 3, null, Arg.Any<CancellationToken>()).Returns([order]);

        var result = await new GetOrderExportRowsHandler(_orders, _currentUser)
            .HandleAsync(new GetOrderExportRows(2, null), CancellationToken.None);

        result.Value.NextCursor.Should().BeNull();
        result.Value.Items.Should().ContainSingle().Which.Should().Be(new OrderExportRow(
            order.Id, "SKU-1", order.Product?.Name, 2, order.Product?.UnitPrice, OrderStatus.Cancelled,
            Now, null, Now.AddHours(1), "Changed my mind."));
    }

    [Fact]
    public async Task GetOrderExportRows_WithMoreRowsThanTheLimit_ReturnsACursor()
    {
        var orders = Enumerable.Range(0, 3)
            .Select(i => Order.Place(Guid.NewGuid(), UserId, 1, Now.AddMinutes(-i), AnOrderedProduct.Any(), "SKU"))
            .ToArray();
        _orders.ListAsync(UserId, 3, null, Arg.Any<CancellationToken>()).Returns(orders);

        var result = await new GetOrderExportRowsHandler(_orders, _currentUser)
            .HandleAsync(new GetOrderExportRows(2, null), CancellationToken.None);

        result.Value.Items.Should().HaveCount(2);
        result.Value.NextCursor.Should().Be(KeysetCursor.Encode(orders[1].PlacedAt, orders[1].Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task GetOrderExportRows_WithALimitOutOfRange_IsAValidationFailure(int limit)
    {
        var result = await new GetOrderExportRowsHandler(_orders, _currentUser)
            .HandleAsync(new GetOrderExportRows(limit, null), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task OrderExportJobEnqueuer_EnqueuesTheBuildForTheOwner()
    {
        var jobs = Substitute.For<IJobScheduler>();
        var exportId = Guid.NewGuid();

        await new OrderExportJobEnqueuer(jobs).HandleAsync(
            new OrderExportRequested(exportId, UserId), new DomainEventContext(Guid.NewGuid(), 1), CancellationToken.None);

        await jobs.Received(1).EnqueueAsync(new BuildOrderExport(exportId, UserId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneOrderExports_SweepsThroughTheRetentionPort()
    {
        var retention = Substitute.For<IOrderExportRetention>();

        await new PruneOrderExportsHandler(retention).Handle(new PruneOrderExports(), CancellationToken.None);

        await retention.Received(1).PruneAsync(Arg.Any<CancellationToken>());
    }
}
