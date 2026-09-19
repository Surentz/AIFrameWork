using AiFramework.Domain.Notifications;
using FluentAssertions;

namespace AiFramework.Domain.Tests.Notifications;

public sealed class NotificationTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReadAt = new(2026, 9, 1, 13, 0, 0, TimeSpan.Zero);

    private static Notification Create(
        Guid? userId = null,
        Guid? sourceMessageId = null,
        NotificationKind kind = NotificationKind.OrderPlaced,
        string title = "Order placed",
        string body = "We have your order.",
        Guid? subjectId = null) =>
        Notification.Create(
            Guid.NewGuid(),
            userId ?? Guid.NewGuid(),
            sourceMessageId ?? Guid.NewGuid(),
            kind,
            title,
            body,
            subjectId,
            CreatedAt);

    [Fact]
    public void Create_WithValidDetails_SetsTheProperties()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();

        var notification = Notification.Create(
            id, userId, messageId, NotificationKind.OrderShipped,
            "Order shipped", "It is on its way.", subjectId, CreatedAt);

        notification.Id.Should().Be(id);
        notification.UserId.Should().Be(userId);
        notification.SourceMessageId.Should().Be(messageId);
        notification.Kind.Should().Be(NotificationKind.OrderShipped);
        notification.Title.Should().Be("Order shipped");
        notification.Body.Should().Be("It is on its way.");
        notification.SubjectId.Should().Be(subjectId);
        notification.CreatedAt.Should().Be(CreatedAt);
    }

    [Fact]
    public void Create_StartsUnread()
    {
        var notification = Create();

        notification.ReadAt.Should().BeNull();
        notification.IsRead.Should().BeFalse();
    }

    [Fact]
    public void Create_TrimsTheTitleAndBody()
    {
        var notification = Create(title: "  Order placed  ", body: "  We have it.  ");

        notification.Title.Should().Be("Order placed");
        notification.Body.Should().Be("We have it.");
    }

    [Fact]
    public void Create_AllowsNoSubject()
    {
        // The subject is a deep-link convenience, not an invariant.
        Create(subjectId: null).SubjectId.Should().BeNull();
    }

    [Fact]
    public void Create_WithNoRecipient_Throws()
    {
        var act = () => Create(userId: Guid.Empty);

        act.Should().Throw<DomainException>().WithMessage("*recipient*");
    }

    [Fact]
    public void Create_WithNoSourceMessage_Throws()
    {
        // Without it the unique index cannot dedupe a redelivery, so an empty one is refused
        // here rather than producing a row that silently duplicates on the next attempt.
        var act = () => Create(sourceMessageId: Guid.Empty);

        act.Should().Throw<DomainException>().WithMessage("*message*");
    }

    [Fact]
    public void Create_WithAKindThatIsNotDefined_Throws()
    {
        var act = () => Create(kind: (NotificationKind)999);

        act.Should().Throw<DomainException>().WithMessage("*not a notification kind*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithNoMeaningfulTitle_Throws(string? title)
    {
        var act = () => Create(title: title!);

        act.Should().Throw<DomainException>().WithMessage("*title*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithNoMeaningfulBody_Throws(string? body)
    {
        var act = () => Create(body: body!);

        act.Should().Throw<DomainException>().WithMessage("*body*");
    }

    [Fact]
    public void Create_WithATitleOverTheLimit_Throws()
    {
        var act = () => Create(title: new string('a', Notification.MaxTitleLength + 1));

        act.Should().Throw<DomainException>().WithMessage("*title*longer*");
    }

    [Fact]
    public void Create_WithABodyOverTheLimit_Throws()
    {
        var act = () => Create(body: new string('a', Notification.MaxBodyLength + 1));

        act.Should().Throw<DomainException>().WithMessage("*body*longer*");
    }

    [Fact]
    public void Create_WithATitleAtTheLimit_Succeeds()
    {
        var title = new string('a', Notification.MaxTitleLength);

        Create(title: title).Title.Should().Be(title);
    }

    [Fact]
    public void MarkRead_OnAnUnreadNotification_RecordsWhen()
    {
        var notification = Create();

        notification.MarkRead(ReadAt);

        notification.ReadAt.Should().Be(ReadAt);
        notification.IsRead.Should().BeTrue();
    }

    [Fact]
    public void MarkRead_OnAnUnreadNotification_ReturnsTrue()
    {
        Create().MarkRead(ReadAt).Should().BeTrue();
    }

    [Fact]
    public void MarkRead_OnAnAlreadyReadNotification_ReturnsFalse()
    {
        var notification = Create();
        notification.MarkRead(ReadAt);

        notification.MarkRead(ReadAt.AddHours(1)).Should().BeFalse();
    }

    [Fact]
    public void MarkRead_OnAnAlreadyReadNotification_KeepsTheOriginalTime()
    {
        // First write wins: a client replaying the call must not drift "when did they read it"
        // later every time.
        var notification = Create();
        notification.MarkRead(ReadAt);

        notification.MarkRead(ReadAt.AddHours(1));

        notification.ReadAt.Should().Be(ReadAt);
    }
}
