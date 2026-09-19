using AiFramework.Application.Notifications;
using AiFramework.Domain.Notifications;

namespace AiFramework.Api.Notifications;

/// <summary>
/// Application view -> HTTP response. Kept beside the DTOs rather than as private methods on the
/// controller so the controller holds nothing but bind-delegate-map actions.
/// </summary>
internal static class NotificationMappings
{
    public static NotificationResponse ToResponse(this NotificationListItem item) => new()
    {
        Id = item.Id,
        Kind = item.Kind,
        Title = item.Title,
        Body = item.Body,
        SubjectId = item.SubjectId,
        CreatedAt = item.CreatedAt,
        ReadAt = item.ReadAt,
    };

    public static NotificationReadResponse ToResponse(this NotificationReadResult result) => new()
    {
        MarkedCount = result.MarkedCount,
        UnreadCount = result.UnreadCount,
    };
}

public sealed record NotificationResponse
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Serialized as its NAME, not its number — <c>Program.cs</c> configures a
    /// <c>JsonStringEnumConverter</c>, and the generated OpenAPI document and
    /// <c>frontend/src/api/schema.d.ts</c> both carry it as a string union. A client switching on
    /// this gets a compile error when a kind is added, rather than an unhandled integer.
    /// </summary>
    public required NotificationKind Kind { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    /// <summary>
    /// The order or product this is about, for deep-linking. Null when the subject is gone or was
    /// never recorded — see <c>Notification.SubjectId</c>.
    /// </summary>
    public Guid? SubjectId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null while unread. The single source of read-ness; there is no separate flag.</summary>
    public DateTimeOffset? ReadAt { get; init; }
}

public sealed record NotificationPageResponse
{
    public required IReadOnlyList<NotificationResponse> Items { get; init; }

    public required string? NextCursor { get; init; }
}

/// <summary>The badge count, on its own so a client polling it transfers one number.</summary>
public sealed record UnreadCountResponse
{
    public required int UnreadCount { get; init; }
}

/// <summary>What both mark-read endpoints answer with.</summary>
public sealed record NotificationReadResponse
{
    /// <summary>
    /// How many this call actually changed. Zero is a success: re-reading something already read
    /// is idempotent, not an error.
    /// </summary>
    public required int MarkedCount { get; init; }

    /// <summary>The badge count after the change, so the client needs no second round-trip.</summary>
    public required int UnreadCount { get; init; }
}
