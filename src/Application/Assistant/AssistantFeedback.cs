namespace AiFramework.Application.Assistant;

public enum AnswerRating
{
    Up,
    Down,
}

/// <summary>Which path answered a question: an instant answer, a help article, suggestions, or the AI.</summary>
public enum AnswerPath
{
    Instant,
    Help,
    Suggested,
    Ai,
}

public enum RateResult
{
    Rated,

    /// <summary>No such conversation for this owner, or no such turn: indistinguishable on purpose.</summary>
    NotFound,

    /// <summary>The turn exists but carries no answer (a question, a tool round).</summary>
    NotAnAnswer,
}

/// <summary>
/// One answered question, for the insights page. Holds no question text and no user id: the text
/// is read live from the transcript, so it goes when the conversation goes (ADR 0037).
/// </summary>
public sealed record AnsweredQuestion(
    Guid ConversationId,
    int QuestionTurn,
    int AnswerTurn,
    DateTimeOffset At,
    AnswerPath Path,
    string? TargetId,
    double? BestScore,
    double? Margin,
    bool Corrected,
    ToolScope Scope);

/// <summary>Tracked and committed by the unit of work with the question it describes.</summary>
public interface IAssistantOutcomeLog
{
    public void Record(AnsweredQuestion question);
}

/// <summary>The owner's 👍/👎 per answer. Kept beside the transcript, never in it (ADR 0033).</summary>
public interface IAssistantRatings
{
    /// <summary>A null rating clears the answer's rating.</summary>
    public Task<RateResult> RateAsync(
        Guid conversationId, Guid ownerId, int turn, AnswerRating? rating, DateTimeOffset at, CancellationToken cancellationToken);
}
