using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Notifications;

/// <summary>
/// One entry in a user's notification feed. Owned, like <see cref="Orders.Order"/> and unlike
/// <see cref="Products.Product"/>: every row belongs to exactly one user, and the repository
/// port is scoped by signature so no caller can read another user's feed.
/// </summary>
public sealed class Notification : Entity
{
    /// <summary>The longest title <see cref="Create"/> accepts, mirrored by the EF mapping.</summary>
    public const int MaxTitleLength = 128;

    /// <summary>The longest body <see cref="Create"/> accepts, mirrored by the EF mapping.</summary>
    public const int MaxBodyLength = 1024;

    private Notification(
        Guid id,
        Guid userId,
        Guid sourceMessageId,
        NotificationKind kind,
        string title,
        string body,
        Guid? subjectId,
        DateTimeOffset createdAt)
    {
        Id = id;
        UserId = userId;
        SourceMessageId = sourceMessageId;
        Kind = kind;
        Title = title;
        Body = body;
        SubjectId = subjectId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    /// <summary>The recipient. Set once; a notification is never reassigned.</summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// The outbox MessageId of the domain event that produced this row. Carried on the entity
    /// rather than left to the handler because it is what makes creation idempotent: delivery is
    /// at-least-once, so every notification handler WILL run twice eventually, and a unique index
    /// on (SourceMessageId, UserId) turns the second run into a no-op insert instead of a
    /// duplicate in someone's feed.
    ///
    /// Scoped by user as well as by message because one event can fan out to many recipients —
    /// a price change notifies everyone who ordered the product — so the message id alone is not
    /// unique per row.
    /// </summary>
    public Guid SourceMessageId { get; private set; }

    public NotificationKind Kind { get; private set; }

    public string Title { get; private set; }

    public string Body { get; private set; }

    /// <summary>
    /// The order or product this is about, for the client to deep-link to. Nullable because it is
    /// a convenience for the reader, not an invariant: a notification whose subject has since
    /// been deleted is still a true statement about something that happened.
    /// </summary>
    public Guid? SubjectId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// When the recipient first read this, or null while it is unread. The nullable timestamp is
    /// the single source of read-ness — there is deliberately no separate bool that could
    /// disagree with it.
    /// </summary>
    public DateTimeOffset? ReadAt { get; private set; }

    public bool IsRead => ReadAt is not null;

    /// <summary>
    /// Handed its id and timestamp rather than reading a clock, the same reason
    /// <see cref="Orders.Order.Place"/> and <see cref="Products.Product.Create"/> are: Domain has
    /// no clock.
    /// </summary>
    public static Notification Create(
        Guid id,
        Guid userId,
        Guid sourceMessageId,
        NotificationKind kind,
        string title,
        string body,
        Guid? subjectId,
        DateTimeOffset createdAt)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException("A notification needs a recipient.");
        }

        if (sourceMessageId == Guid.Empty)
        {
            throw new DomainException("A notification needs the message that caused it.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new DomainException($"'{kind}' is not a notification kind.");
        }

        ValidateTitle(title);
        ValidateBody(body);

        return new Notification(
            id, userId, sourceMessageId, kind, title.Trim(), body.Trim(), subjectId, createdAt);
    }

    /// <summary>
    /// First write wins, and a second call is a no-op rather than an error. Two reasons, both
    /// about honesty: "when did they read it" should not drift later every time a client replays
    /// the call, and marking an already-read notification read is not a broken invariant — it is
    /// a client being imprecise, which <see cref="DomainException"/> is explicitly not for.
    /// </summary>
    /// <returns>
    /// True when this call is what marked it read, false when it already was. Lets the caller
    /// report how many it actually changed without re-reading the entity.
    /// </returns>
    public bool MarkRead(DateTimeOffset readAt)
    {
        if (IsRead)
        {
            return false;
        }

        ReadAt = readAt;
        return true;
    }

    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainException("A notification needs a title.");
        }

        if (title.Trim().Length > MaxTitleLength)
        {
            throw new DomainException($"A title cannot be longer than {MaxTitleLength} characters.");
        }
    }

    private static void ValidateBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new DomainException("A notification needs a body.");
        }

        if (body.Trim().Length > MaxBodyLength)
        {
            throw new DomainException($"A body cannot be longer than {MaxBodyLength} characters.");
        }
    }
}
