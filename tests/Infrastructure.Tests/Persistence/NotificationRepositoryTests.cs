using AiFramework.Domain.Notifications;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Persistence;

/// <summary>
/// The notification repository and its mapping, against the real engine.
/// </summary>
/// <remarks>
/// What the Api integration tests cannot reach, and why this class exists: the unique index
/// actually rejecting a duplicate (they exercise the in-handler dedupe read instead, which is an
/// optimization in front of the real guarantee), the partial index's filter matching the query's
/// own predicate, and unreadOnly combined with a cursor. Each of those fails silently if it
/// regresses — a wrong index is a performance cliff, not an error.
///
/// Every test owns a disjoint user, because Infrastructure.Tests shares one container by policy
/// (tests/CLAUDE.md) and a test that assumes it owns the table is wrong by construction.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class NotificationRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Base = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Notification ANotification(
        Guid userId,
        Guid? messageId = null,
        NotificationKind kind = NotificationKind.OrderPlaced,
        DateTimeOffset? createdAt = null,
        Guid? id = null) =>
        Notification.Create(
            id ?? Guid.NewGuid(),
            userId,
            messageId ?? Guid.NewGuid(),
            kind,
            "Order placed",
            "We have your order.",
            Guid.NewGuid(),
            createdAt ?? Base);

    private async Task SeedAsync(params Notification[] notifications)
    {
        await using var context = fixture.CreateContext();
        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();
    }

    private static NotificationRepository Repository(AiFrameworkDbContext context) => new(context);

    [Fact]
    public async Task AddAsync_RoundTripsEveryMappedProperty()
    {
        var userId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var id = Guid.NewGuid();

        await SeedAsync(Notification.Create(
            id, userId, messageId, NotificationKind.ProductPriceChanged,
            "Price changed", "Widget rose from 9.99 to 12.50.", subjectId, Base));

        await using var context = fixture.CreateContext();
        var stored = await context.Notifications.AsNoTracking().SingleAsync(n => n.Id == id);

        stored.UserId.Should().Be(userId);
        stored.SourceMessageId.Should().Be(messageId);
        stored.Kind.Should().Be(NotificationKind.ProductPriceChanged);
        stored.Title.Should().Be("Price changed");
        stored.Body.Should().Be("Widget rose from 9.99 to 12.50.");
        stored.SubjectId.Should().Be(subjectId);
        stored.CreatedAt.Should().Be(Base);
        stored.ReadAt.Should().BeNull();
    }

    [Fact]
    public async Task Kind_IsStoredAsItsName()
    {
        // Persisted by name, not by number — NotificationKind says the names are the stored
        // contract, and an int column would silently reinterpret every row if a member moved.
        var id = Guid.NewGuid();
        await SeedAsync(ANotification(Guid.NewGuid(), kind: NotificationKind.OrderCancelled, id: id));

        await using var context = fixture.CreateContext();
        var stored = await context.Database
            .SqlQuery<string>($"""SELECT "Kind" AS "Value" FROM notifications WHERE "Id" = {id}""")
            .SingleAsync();

        stored.Should().Be("OrderCancelled");
    }

    [Fact]
    public async Task TheUniqueIndex_RejectsASecondRowForTheSameMessageRecipientAndKind()
    {
        // THE guarantee. The dedupe read in NotificationFanOut is an optimization in front of
        // this; two concurrent deliveries can both pass that check, and this is what stops both
        // landing.
        var userId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await SeedAsync(ANotification(userId, messageId));

        var act = async () => await SeedAsync(ANotification(userId, messageId));

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task TheUniqueIndex_AllowsADifferentKindFromTheSameMessage()
    {
        // The reason Kind is part of the key: one event may have more than one notifier, and
        // without Kind the second one's row collides and is lost permanently on the retry.
        var userId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await SeedAsync(ANotification(userId, messageId, NotificationKind.OrderPlaced));

        var act = async () => await SeedAsync(
            ANotification(userId, messageId, NotificationKind.OrderShipped));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TheUniqueIndex_AllowsTheSameMessageForADifferentRecipient()
    {
        // A price change fans one message out to every purchaser.
        var messageId = Guid.NewGuid();
        await SeedAsync(ANotification(Guid.NewGuid(), messageId));

        var act = async () => await SeedAsync(ANotification(Guid.NewGuid(), messageId));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListNotifiedRecipientsAsync_ReturnsOnlyThatMessageAndKind()
    {
        var messageId = Guid.NewGuid();
        var placedRecipient = Guid.NewGuid();
        var shippedRecipient = Guid.NewGuid();
        await SeedAsync(
            ANotification(placedRecipient, messageId, NotificationKind.OrderPlaced),
            ANotification(shippedRecipient, messageId, NotificationKind.OrderShipped));

        await using var context = fixture.CreateContext();
        var recipients = await Repository(context).ListNotifiedRecipientsAsync(
            messageId, NotificationKind.OrderPlaced, CancellationToken.None);

        recipients.Should().BeEquivalentTo([placedRecipient]);
    }

    [Fact]
    public async Task CountUnreadAsync_CountsOnlyTheOwnersUnread()
    {
        var owner = Guid.NewGuid();
        var read = ANotification(owner, createdAt: Base.AddMinutes(1));
        read.MarkRead(Base.AddMinutes(2));
        await SeedAsync(
            ANotification(owner, createdAt: Base),
            read,
            ANotification(Guid.NewGuid(), createdAt: Base));

        await using var context = fixture.CreateContext();
        var count = await Repository(context).CountUnreadAsync(owner, CancellationToken.None);

        count.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_UnreadOnly_ExcludesRead()
    {
        // Filters in SQL against the partial index rather than in the handler.
        var owner = Guid.NewGuid();
        var read = ANotification(owner, createdAt: Base.AddMinutes(1));
        read.MarkRead(Base.AddMinutes(2));
        await SeedAsync(ANotification(owner, createdAt: Base), read);

        await using var context = fixture.CreateContext();
        var rows = await Repository(context).ListAsync(
            owner, unreadOnly: true, limit: 10, after: null, CancellationToken.None);

        rows.Should().HaveCount(1);
        rows[0].IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_UnreadOnlyWithACursor_KeepsTheFilterAndThePage()
    {
        // The combination the Api tests cannot reach: an unread-only page taken from a cursor.
        // Getting either half wrong here silently returns read rows or skips unread ones.
        var owner = Guid.NewGuid();
        var newest = ANotification(owner, createdAt: Base.AddMinutes(3), id: Guid.NewGuid());
        var middleRead = ANotification(owner, createdAt: Base.AddMinutes(2));
        middleRead.MarkRead(Base.AddMinutes(9));
        var oldest = ANotification(owner, createdAt: Base.AddMinutes(1), id: Guid.NewGuid());
        await SeedAsync(newest, middleRead, oldest);

        await using var context = fixture.CreateContext();
        var rows = await Repository(context).ListAsync(
            owner,
            unreadOnly: true,
            limit: 10,
            after: (newest.CreatedAt, newest.Id),
            CancellationToken.None);

        // Everything strictly older than the cursor, minus the read one in between.
        rows.Should().ContainSingle().Which.Id.Should().Be(oldest.Id);
    }

    [Fact]
    public async Task ListAsync_OrdersNewestFirst()
    {
        var owner = Guid.NewGuid();
        var older = ANotification(owner, createdAt: Base.AddMinutes(1));
        var newer = ANotification(owner, createdAt: Base.AddMinutes(2));
        await SeedAsync(older, newer);

        await using var context = fixture.CreateContext();
        var rows = await Repository(context).ListAsync(
            owner, unreadOnly: false, limit: 10, after: null, CancellationToken.None);

        rows.Select(n => n.Id).Should().ContainInOrder(newer.Id, older.Id);
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyTheOwnersRows()
    {
        var owner = Guid.NewGuid();
        await SeedAsync(ANotification(owner), ANotification(Guid.NewGuid()));

        await using var context = fixture.CreateContext();
        var rows = await Repository(context).ListAsync(
            owner, unreadOnly: false, limit: 10, after: null, CancellationToken.None);

        rows.Should().OnlyContain(n => n.UserId == owner);
    }

    [Fact]
    public async Task GetForUpdateAsync_ReturnsATrackedEntity()
    {
        // The whole reason it is a separate method: MarkNotificationReadHandler mutates what this
        // returns, and an AsNoTracking read would drop that write with no error anywhere.
        var owner = Guid.NewGuid();
        var notification = ANotification(owner);
        await SeedAsync(notification);

        await using var context = fixture.CreateContext();
        var loaded = await Repository(context)
            .GetForUpdateAsync(notification.Id, owner, CancellationToken.None);

        loaded.Should().NotBeNull();
        context.Entry(loaded).State.Should().NotBe(EntityState.Detached);
    }

    [Fact]
    public async Task GetForUpdateAsync_MarkingRead_Persists()
    {
        var owner = Guid.NewGuid();
        var notification = ANotification(owner);
        await SeedAsync(notification);

        await using (var context = fixture.CreateContext())
        {
            var loaded = await Repository(context)
                .GetForUpdateAsync(notification.Id, owner, CancellationToken.None);
            loaded!.MarkRead(Base.AddHours(1));
            await context.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContext();
        var stored = await verify.Notifications.AsNoTracking()
            .SingleAsync(n => n.Id == notification.Id);
        stored.ReadAt.Should().Be(Base.AddHours(1));
    }

    [Fact]
    public async Task GetForUpdateAsync_ForAnotherUser_ReturnsNull()
    {
        var notification = ANotification(Guid.NewGuid());
        await SeedAsync(notification);

        await using var context = fixture.CreateContext();
        var loaded = await Repository(context)
            .GetForUpdateAsync(notification.Id, Guid.NewGuid(), CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task ListUnreadForUpdateAsync_ReturnsTrackedUnreadRowsOnly()
    {
        var owner = Guid.NewGuid();
        var read = ANotification(owner, createdAt: Base.AddMinutes(1));
        read.MarkRead(Base.AddMinutes(2));
        await SeedAsync(ANotification(owner, createdAt: Base), read);

        await using var context = fixture.CreateContext();
        var rows = await Repository(context)
            .ListUnreadForUpdateAsync(owner, CancellationToken.None);

        rows.Should().ContainSingle();
        context.Entry(rows[0]).State.Should().NotBe(EntityState.Detached);
    }
}
