namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// One assistant conversation. Its xmin row version (a shadow property, see the configuration) is
/// what turns two concurrent questions into one success and one 409. Deleted with its turns after
/// a day of inactivity. ADR 0033.
/// </summary>
public sealed class AssistantConversationRow
{
    public required Guid Id { get; init; }

    public required Guid OwnerId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastActiveAt { get; set; }

    public int QuestionCount { get; set; }

    /// <summary>How many turns are stored, which is also the next turn's <see cref="AssistantTurnRow.Seq"/>.</summary>
    public int TurnCount { get; set; }
}

/// <summary>
/// One transcript entry. <see cref="Payload"/> is text, never jsonb: a model turn is replayed to
/// the provider exactly as stored, and jsonb would reorder its keys. Rows are never updated.
/// </summary>
public sealed class AssistantTurnRow
{
    public required Guid Id { get; init; }

    public required Guid ConversationId { get; init; }

    public required int Seq { get; init; }

    public required string Kind { get; init; }

    public required string Payload { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
