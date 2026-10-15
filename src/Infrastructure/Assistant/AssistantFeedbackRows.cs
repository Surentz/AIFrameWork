using AiFramework.Application.Assistant;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>The owner's rating of one answer. Deleted with its conversation.</summary>
public sealed class AssistantRatingRow
{
    public required Guid ConversationId { get; init; }

    /// <summary>The <see cref="AssistantTurnRow.Seq"/> of the answer's turn.</summary>
    public required int Turn { get; init; }

    public required AnswerRating Rating { get; set; }

    public required DateTimeOffset RatedAt { get; set; }
}

/// <summary>One answered question's routing outcome. No question text, no user id (ADR 0037).</summary>
public sealed class AssistantOutcomeRow
{
    public required Guid Id { get; init; }

    public required Guid ConversationId { get; init; }

    public required int QuestionTurn { get; init; }

    public required int AnswerTurn { get; init; }

    public required DateTimeOffset At { get; init; }

    public required AnswerPath Path { get; init; }

    /// <summary>The intent id or help article id the router chose; null for the AI and for nothing.</summary>
    public string? TargetId { get; init; }

    public double? BestScore { get; init; }

    public double? Margin { get; init; }

    public required bool Corrected { get; init; }

    public required ToolScope Scope { get; init; }
}
