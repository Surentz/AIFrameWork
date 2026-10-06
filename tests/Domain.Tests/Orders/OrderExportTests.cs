using AiFramework.Domain.Orders;
using FluentAssertions;

namespace AiFramework.Domain.Tests.Orders;

public sealed class OrderExportTests
{
    private static readonly byte[] AFile = [0x25, 0x50, 0x44, 0x46]; // "%PDF"
    private static readonly byte[] AnotherFile = [0x25, 0x50, 0x44, 0x46, 0x2D];
    private static readonly DateTimeOffset RequestedAt = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = RequestedAt.AddMinutes(2);

    private static OrderExport Requested(Guid? userId = null)
    {
        var export = OrderExport.Request(Guid.NewGuid(), userId ?? Guid.NewGuid(), RequestedAt);

        // Request raises OrderExportRequested; clearing keeps each test's assertion about the
        // event IT caused.
        export.ClearDomainEvents();
        return export;
    }

    [Fact]
    public void Request_StartsRequestedWithNoFile()
    {
        var export = OrderExport.Request(Guid.NewGuid(), Guid.NewGuid(), RequestedAt);

        export.Status.Should().Be(OrderExportStatus.Requested);
        export.RequestedAt.Should().Be(RequestedAt);
        export.Document.Should().BeNull();
        export.RowCount.Should().BeNull();
        export.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void Request_RaisesOrderExportRequested()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var export = OrderExport.Request(id, userId, RequestedAt);

        export.DomainEvents.Should().ContainSingle()
            .Which.Should().Be(new OrderExportRequested(id, userId));
    }

    [Fact]
    public void Request_WithNoUser_Throws()
    {
        var act = () => OrderExport.Request(Guid.NewGuid(), Guid.Empty, RequestedAt);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Complete_FromRequested_StoresTheFile()
    {
        var export = Requested();

        export.Complete(AFile, 0, CompletedAt);

        export.Status.Should().Be(OrderExportStatus.Ready);
        export.Document.Should().Equal(AFile);
        export.RowCount.Should().Be(0);
        export.CompletedAt.Should().Be(CompletedAt);
    }

    [Fact]
    public void Complete_FromRequested_RaisesOrderExportCompleted()
    {
        var userId = Guid.NewGuid();
        var export = Requested(userId);

        export.Complete(AFile, 42, CompletedAt);

        export.DomainEvents.Should().ContainSingle()
            .Which.Should().Be(new OrderExportCompleted(export.Id, userId, 42));
    }

    [Fact]
    public void Complete_WhenAlreadyReady_ChangesNothing()
    {
        var export = Requested();
        export.Complete(AFile, 1, CompletedAt);
        export.ClearDomainEvents();

        export.Complete(AnotherFile, 2, CompletedAt.AddMinutes(1));

        export.Document.Should().Equal(AFile);
        export.RowCount.Should().Be(1);
        export.CompletedAt.Should().Be(CompletedAt);
    }

    [Fact]
    public void Complete_WhenAlreadyReady_RaisesNothing()
    {
        var export = Requested();
        export.Complete(AFile, 1, CompletedAt);
        export.ClearDomainEvents();

        export.Complete(AnotherFile, 2, CompletedAt.AddMinutes(1));

        export.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Complete_WithANegativeRowCount_Throws()
    {
        var export = Requested();

        var act = () => export.Complete(AFile, -1, CompletedAt);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Complete_WithNoDocument_Throws()
    {
        var export = Requested();

        // null! deliberately breaks the non-nullable contract to prove the runtime guard holds.
        var act = () => export.Complete(null!, 0, CompletedAt);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Complete_WithAnEmptyDocument_Throws()
    {
        var export = Requested();

        var act = () => export.Complete([], 0, CompletedAt);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void IsInProgress_JustBeforeStaleAfter_IsTrue()
    {
        var export = Requested();

        var now = RequestedAt + OrderExport.StaleAfter - TimeSpan.FromSeconds(1);

        export.IsInProgress(now).Should().BeTrue();
        export.IsFailed(now).Should().BeFalse();
    }

    [Fact]
    public void IsFailed_AtStaleAfter_IsTrue()
    {
        var export = Requested();

        var now = RequestedAt + OrderExport.StaleAfter;

        export.IsFailed(now).Should().BeTrue();
        export.IsInProgress(now).Should().BeFalse();
    }

    [Fact]
    public void IsFailed_WhenReady_IsFalseHoweverOld()
    {
        var export = Requested();
        export.Complete(AFile, 1, CompletedAt);

        var muchLater = RequestedAt.AddDays(6);

        export.IsFailed(muchLater).Should().BeFalse();
        export.IsInProgress(muchLater).Should().BeFalse();
    }

    [Fact]
    public void StaleAfter_IsFortyFiveMinutes()
    {
        OrderExport.StaleAfter.Should().Be(TimeSpan.FromMinutes(45));
    }
}
