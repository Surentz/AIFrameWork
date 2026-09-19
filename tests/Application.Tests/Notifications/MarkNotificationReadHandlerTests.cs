using AiFramework.Application.Abstractions;
using AiFramework.Application.Notifications;
using AiFramework.Domain.Notifications;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Notifications;

public sealed class MarkNotificationReadHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly INotificationRepository _repository =
        Substitute.For<INotificationRepository>();

    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public MarkNotificationReadHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _currentUser.Id.Returns(UserId);
    }

    private static Notification ANotification() => Notification.Create(
        Guid.NewGuid(), UserId, Guid.NewGuid(), NotificationKind.OrderPlaced,
        "Order placed", "We have your order.", Guid.NewGuid(), Now.AddHours(-1));

    private MarkNotificationReadHandler Handler() =>
        new(_repository, _currentUser, _clock);

    [Fact]
    public async Task HandleAsync_WithAnUnreadNotification_MarksItRead()
    {
        var notification = ANotification();
        _repository.GetForUpdateAsync(notification.Id, UserId, Arg.Any<CancellationToken>())
            .Returns(notification);

        var result = await Handler().HandleAsync(
            new MarkNotificationRead(notification.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        notification.ReadAt.Should().Be(Now);
    }

    [Fact]
    public async Task HandleAsync_WithAnUnreadNotification_ReportsOneMarked()
    {
        var notification = ANotification();
        _repository.GetForUpdateAsync(notification.Id, UserId, Arg.Any<CancellationToken>())
            .Returns(notification);

        var result = await Handler().HandleAsync(
            new MarkNotificationRead(notification.Id), CancellationToken.None);

        result.Value.MarkedCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_WithAnAlreadyReadNotification_ReportsNoneMarked()
    {
        // Idempotent: a replayed call is a success with nothing changed, not a failure.
        var notification = ANotification();
        notification.MarkRead(Now.AddMinutes(-5));
        _repository.GetForUpdateAsync(notification.Id, UserId, Arg.Any<CancellationToken>())
            .Returns(notification);

        var result = await Handler().HandleAsync(
            new MarkNotificationRead(notification.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.MarkedCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WhenItMarksOneRead_SubtractsItFromTheStoredCount()
    {
        // CountUnreadAsync answers from the last committed state, which still includes the row
        // this call just marked - the unit-of-work behavior has not committed yet. The handler
        // subtracts what it changed so the caller gets the number they will actually see next.
        var notification = ANotification();
        _repository.GetForUpdateAsync(notification.Id, UserId, Arg.Any<CancellationToken>())
            .Returns(notification);
        _repository.CountUnreadAsync(UserId, Arg.Any<CancellationToken>()).Returns(4);

        var result = await Handler().HandleAsync(
            new MarkNotificationRead(notification.Id), CancellationToken.None);

        result.Value.UnreadCount.Should().Be(3);
    }

    [Fact]
    public async Task HandleAsync_WhenNothingChanged_DoesNotSubtractFromTheStoredCount()
    {
        var notification = ANotification();
        notification.MarkRead(Now.AddMinutes(-5));
        _repository.GetForUpdateAsync(notification.Id, UserId, Arg.Any<CancellationToken>())
            .Returns(notification);
        _repository.CountUnreadAsync(UserId, Arg.Any<CancellationToken>()).Returns(4);

        var result = await Handler().HandleAsync(
            new MarkNotificationRead(notification.Id), CancellationToken.None);

        result.Value.UnreadCount.Should().Be(4);
    }

    [Fact]
    public async Task HandleAsync_WithAnIdThatDoesNotExist_ReturnsNotFound()
    {
        _repository.GetForUpdateAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Notification?)null);

        var result = await Handler().HandleAsync(
            new MarkNotificationRead(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task HandleAsync_ReadsScopedToTheCaller()
    {
        // The owner is passed to the repository, which is what stops one caller marking
        // another's notification read. A notification belonging to someone else comes back null
        // and becomes the same 404 as one that never existed.
        var id = Guid.NewGuid();
        _repository.GetForUpdateAsync(id, UserId, Arg.Any<CancellationToken>())
            .Returns((Notification?)null);

        await Handler().HandleAsync(new MarkNotificationRead(id), CancellationToken.None);

        await _repository.Received(1).GetForUpdateAsync(
            id, UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithNoSession_ReturnsUnauthorized()
    {
        _currentUser.Id.Returns((Guid?)null);

        var result = await Handler().HandleAsync(
            new MarkNotificationRead(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }
}

public sealed class MarkAllNotificationsReadHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly INotificationRepository _repository =
        Substitute.For<INotificationRepository>();

    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public MarkAllNotificationsReadHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _currentUser.Id.Returns(UserId);
    }

    private static Notification ANotification() => Notification.Create(
        Guid.NewGuid(), UserId, Guid.NewGuid(), NotificationKind.OrderPlaced,
        "Order placed", "We have your order.", null, Now.AddHours(-1));

    private MarkAllNotificationsReadHandler Handler() =>
        new(_repository, _currentUser, _clock);

    [Fact]
    public async Task HandleAsync_MarksEveryUnreadNotification()
    {
        var first = ANotification();
        var second = ANotification();
        _repository.ListUnreadForUpdateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([first, second]);

        await Handler().HandleAsync(new MarkAllNotificationsRead(), CancellationToken.None);

        first.ReadAt.Should().Be(Now);
        second.ReadAt.Should().Be(Now);
    }

    [Fact]
    public async Task HandleAsync_ReportsHowManyItMarked()
    {
        _repository.ListUnreadForUpdateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([ANotification(), ANotification(), ANotification()]);

        var result = await Handler().HandleAsync(
            new MarkAllNotificationsRead(), CancellationToken.None);

        result.Value.MarkedCount.Should().Be(3);
    }

    [Fact]
    public async Task HandleAsync_LeavesTheUnreadCountAtZero()
    {
        // Everything unread was just marked, so zero is true by construction rather than by
        // asking the database again.
        _repository.ListUnreadForUpdateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([ANotification()]);

        var result = await Handler().HandleAsync(
            new MarkAllNotificationsRead(), CancellationToken.None);

        result.Value.UnreadCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_UsesOneTimestampForTheWholeBatch()
    {
        // They were all read by the same gesture; re-reading the clock per row would spread them
        // across an interval that never happened.
        var first = ANotification();
        var second = ANotification();
        _repository.ListUnreadForUpdateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([first, second]);

        await Handler().HandleAsync(new MarkAllNotificationsRead(), CancellationToken.None);

        first.ReadAt.Should().Be(second.ReadAt);
    }

    [Fact]
    public async Task HandleAsync_WithNothingUnread_ReportsNoneMarked()
    {
        _repository.ListUnreadForUpdateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await Handler().HandleAsync(
            new MarkAllNotificationsRead(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.MarkedCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WithNoSession_ReturnsUnauthorized()
    {
        _currentUser.Id.Returns((Guid?)null);

        var result = await Handler().HandleAsync(
            new MarkAllNotificationsRead(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }
}
