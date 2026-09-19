using AiFramework.Application.Abstractions;
using AiFramework.Application.Notifications;
using AiFramework.Application.Orders;
using AiFramework.Domain.Notifications;
using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Notifications;

/// <summary>
/// The four domain-event handlers that write the feed. Delivery is at-least-once, so the
/// idempotency assertions here are the load-bearing ones.
/// </summary>
public sealed class NotifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MessageId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly DomainEventContext Context = new(MessageId, Attempt: 1);

    private readonly INotificationRepository _notifications =
        Substitute.For<INotificationRepository>();

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly INotificationPush _push = Substitute.For<INotificationPush>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public NotifierTests()
    {
        _clock.UtcNow.Returns(Now);
        _notifications.ListNotifiedRecipientsAsync(MessageId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        _orders.GetOwnerAsync(OrderId, Arg.Any<CancellationToken>()).Returns(UserId);
    }

    [Fact]
    public async Task OrderPlaced_WritesANotificationForTheBuyer()
    {
        var notifier = new OrderPlacedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new OrderPlaced(OrderId, "SKU-1", 2), Context, CancellationToken.None);

        await _notifications.Received(1).AddAsync(
            Arg.Is<Notification>(n =>
                n.UserId == UserId
                && n.Kind == NotificationKind.OrderPlaced
                && n.SubjectId == OrderId
                && n.SourceMessageId == MessageId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPlaced_CommitsWhatItWrote()
    {
        // The bug this pins: a domain event handler runs on the outbox pump, outside the command
        // pipeline, so no unit-of-work behavior commits for it. Adding without saving writes
        // NOTHING while the outbox row is still marked Processed - silent, permanent loss.
        var notifier = new OrderPlacedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new OrderPlaced(OrderId, "SKU-1", 2), Context, CancellationToken.None);

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProductPriceChanged_CommitsTheWholeFanOutOnce()
    {
        // One save for the batch, not one per recipient.
        _orders.ListPurchaserIdsAsync("SKU-1", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);
        var notifier = new ProductPriceChangedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new ProductPriceChanged(OrderId, "SKU-1", "Widget", 9.99m, 12.50m),
            Context,
            CancellationToken.None);

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPlaced_PushesWhatItWrote()
    {
        var notifier = new OrderPlacedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new OrderPlaced(OrderId, "SKU-1", 2), Context, CancellationToken.None);

        await _push.Received(1).PushAsync(
            UserId,
            Arg.Is<NotificationListItem>(n => n.Kind == NotificationKind.OrderPlaced),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPlaced_PushesOnlyAfterTheCommit()
    {
        // A client told about a notification whose commit then failed would fetch the feed and
        // not find it. The row must be durable before anyone is told it exists.
        var notifier = new OrderPlacedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new OrderPlaced(OrderId, "SKU-1", 2), Context, CancellationToken.None);

        // MA0134: inside Received.InOrder these are call SPECIFICATIONS that NSubstitute matches
        // against its recorded calls, not invocations that produce work to await. There is no
        // Task here to observe - awaiting them is what would be wrong.
#pragma warning disable MA0134
        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            _push.PushAsync(
                Arg.Any<Guid>(), Arg.Any<NotificationListItem>(), Arg.Any<CancellationToken>());
        });
#pragma warning restore MA0134
    }

    [Fact]
    public async Task OrderPlaced_WithNoPushTransport_StillWrites()
    {
        // An empty sequence is a legitimate configuration - realtime simply not wired up - and
        // must never stop the feed being written. See INotificationPush's remarks.
        var notifier = new OrderPlacedNotifier(_notifications, _orders, _unitOfWork, [], _clock);

        await notifier.HandleAsync(
            new OrderPlaced(OrderId, "SKU-1", 2), Context, CancellationToken.None);

        await _notifications.Received(1).AddAsync(
            Arg.Any<Notification>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPlaced_WhenNothingWasWritten_PushesNothing()
    {
        // Nobody should be told about a redelivery that changed nothing.
        _notifications.ListNotifiedRecipientsAsync(MessageId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { UserId });
        var notifier = new OrderPlacedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new OrderPlaced(OrderId, "SKU-1", 2), Context, CancellationToken.None);

        await _push.DidNotReceive().PushAsync(
            Arg.Any<Guid>(), Arg.Any<NotificationListItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPlaced_WhenNothingWasWritten_DoesNotCommit()
    {
        // A redelivery that writes nothing should not issue a pointless round trip.
        _notifications.ListNotifiedRecipientsAsync(MessageId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { UserId });
        var notifier = new OrderPlacedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new OrderPlaced(OrderId, "SKU-1", 2), Context, CancellationToken.None);

        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPlaced_WhenAlreadyNotified_WritesNothing()
    {
        // The redelivery case. At-least-once means this WILL happen.
        _notifications.ListNotifiedRecipientsAsync(MessageId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { UserId });
        var notifier = new OrderPlacedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new OrderPlaced(OrderId, "SKU-1", 2), Context, CancellationToken.None);

        await _notifications.DidNotReceive().AddAsync(
            Arg.Any<Notification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPlaced_WhenTheOrderIsGone_WritesNothing()
    {
        // Throwing here would retry until the message dead-letters over a row that is never
        // coming back.
        _orders.GetOwnerAsync(OrderId, Arg.Any<CancellationToken>()).Returns((Guid?)null);
        var notifier = new OrderPlacedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new OrderPlaced(OrderId, "SKU-1", 2), Context, CancellationToken.None);

        await _notifications.DidNotReceive().AddAsync(
            Arg.Any<Notification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderShipped_NotifiesTheRecipientOnTheEventWithoutALookup()
    {
        // OrderShipped carries its own UserId, unlike OrderPlaced.
        var notifier = new OrderShippedNotifier(_notifications, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new OrderShipped(OrderId, UserId, "SKU-1"), Context, CancellationToken.None);

        await _notifications.Received(1).AddAsync(
            Arg.Is<Notification>(n =>
                n.UserId == UserId && n.Kind == NotificationKind.OrderShipped),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderShipped_WhenAlreadyNotified_WritesNothing()
    {
        _notifications.ListNotifiedRecipientsAsync(MessageId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { UserId });
        var notifier = new OrderShippedNotifier(_notifications, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new OrderShipped(OrderId, UserId, "SKU-1"), Context, CancellationToken.None);

        await _notifications.DidNotReceive().AddAsync(
            Arg.Any<Notification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderCancelled_IncludesTheReasonInTheBody()
    {
        var notifier = new OrderCancelledNotifier(_notifications, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new OrderCancelled(OrderId, UserId, "SKU-1", "Out of stock."),
            Context,
            CancellationToken.None);

        await _notifications.Received(1).AddAsync(
            Arg.Is<Notification>(n =>
                n.Kind == NotificationKind.OrderCancelled
                && n.Body.Contains("Out of stock.", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProductPriceChanged_NotifiesEveryPastPurchaser()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _orders.ListPurchaserIdsAsync("SKU-1", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([first, second]);
        var notifier = new ProductPriceChangedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new ProductPriceChanged(OrderId, "SKU-1", "Widget", 9.99m, 12.50m),
            Context,
            CancellationToken.None);

        await _notifications.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.UserId == first), Arg.Any<CancellationToken>());
        await _notifications.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.UserId == second), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProductPriceChanged_SkipsOnlyTheRecipientsAlreadyNotified()
    {
        // A retry that failed part-way through a fan-out must finish the rest, not start over
        // and not skip everyone.
        var alreadyDone = Guid.NewGuid();
        var stillPending = Guid.NewGuid();
        _orders.ListPurchaserIdsAsync("SKU-1", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([alreadyDone, stillPending]);
        _notifications.ListNotifiedRecipientsAsync(MessageId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { alreadyDone });
        var notifier = new ProductPriceChangedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new ProductPriceChanged(OrderId, "SKU-1", "Widget", 9.99m, 12.50m),
            Context,
            CancellationToken.None);

        await _notifications.DidNotReceive().AddAsync(
            Arg.Is<Notification>(n => n.UserId == alreadyDone), Arg.Any<CancellationToken>());
        await _notifications.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.UserId == stillPending), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProductPriceChanged_CapsTheFanOut()
    {
        _orders.ListPurchaserIdsAsync("SKU-1", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var notifier = new ProductPriceChangedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new ProductPriceChanged(OrderId, "SKU-1", "Widget", 9.99m, 12.50m),
            Context,
            CancellationToken.None);

        // The cap is passed to the repository rather than applied after fetching, so the bound
        // is on the query, not just on the loop.
        await _orders.Received(1).ListPurchaserIdsAsync(
            "SKU-1", ProductPriceChangedNotifier.MaxRecipients, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProductPriceChanged_WithNoPurchasers_WritesNothingAndAsksNothing()
    {
        // No recipients means the dedupe query is pointless too - NotificationFanOut returns
        // before issuing it.
        _orders.ListPurchaserIdsAsync("SKU-1", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var notifier = new ProductPriceChangedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new ProductPriceChanged(OrderId, "SKU-1", "Widget", 9.99m, 12.50m),
            Context,
            CancellationToken.None);

        await _notifications.DidNotReceive().AddAsync(
            Arg.Any<Notification>(), Arg.Any<CancellationToken>());
        await _notifications.DidNotReceive().ListNotifiedRecipientsAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(9.99, 12.50, "rose")]
    [InlineData(12.50, 9.99, "dropped")]
    public async Task ProductPriceChanged_DescribesTheDirectionOfTheChange(
        double oldPrice, double newPrice, string expected)
    {
        _orders.ListPurchaserIdsAsync("SKU-1", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([UserId]);
        var notifier = new ProductPriceChangedNotifier(_notifications, _orders, _unitOfWork, [_push], _clock);

        await notifier.HandleAsync(
            new ProductPriceChanged(
                OrderId, "SKU-1", "Widget", (decimal)oldPrice, (decimal)newPrice),
            Context,
            CancellationToken.None);

        await _notifications.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.Body.Contains(expected, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }
}
