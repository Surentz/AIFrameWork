using System.Text.Json;
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>The one mapping between a transcript turn and its stored row, both ways.</summary>
internal static class AssistantTurnCodec
{
    public const string Question = "question";
    public const string Model = "model";
    public const string ToolResults = "tool_results";
    public const string Quick = "quick_answer";
    public const string Suggested = "suggestions";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AssistantTurnRow Encode(Guid conversationId, int seq, AssistantTurn turn, DateTimeOffset at)
    {
        var (kind, payload) = turn switch
        {
            UserQuestionTurn q => (Question, JsonSerializer.Serialize(new StoredQuestion(q.Text, q.AskedAt), Json)),
            ModelTurn m => (Model, m.Blocks),
            ToolResultsTurn r => (ToolResults, JsonSerializer.Serialize(r.Results, Json)),
            QuickAnswerTurn a => (Quick, JsonSerializer.Serialize(new StoredQuickAnswer(a.Answer, a.Replay), Json)),
            SuggestionsTurn s => (Suggested, JsonSerializer.Serialize(new StoredSuggestions(s.Suggestions, s.Replay), Json)),
            _ => throw new ArgumentOutOfRangeException(nameof(turn), turn.GetType().Name, "Unknown turn type."),
        };

        return new AssistantTurnRow
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Seq = seq,
            Kind = kind,
            Payload = payload,
            CreatedAt = at,
        };
    }

    public static AssistantTurn Decode(AssistantTurnRow row) => row.Kind switch
    {
        Question => DecodeQuestion(row),
        Model => new ModelTurn(row.Payload),
        ToolResults => new ToolResultsTurn(Deserialize<List<AssistantToolResult>>(row)),
        Quick => DecodeQuick(row),
        Suggested => DecodeSuggestions(row),
        _ => throw new InvalidOperationException($"Turn {row.Id} has unknown kind '{row.Kind}'."),
    };

    private static UserQuestionTurn DecodeQuestion(AssistantTurnRow row)
    {
        var question = Deserialize<StoredQuestion>(row);
        return new UserQuestionTurn(question.Text, question.AskedAt);
    }

    private static QuickAnswerTurn DecodeQuick(AssistantTurnRow row)
    {
        var stored = Deserialize<StoredQuickAnswer>(row);
        return new QuickAnswerTurn(
            stored.Answer ?? throw Incomplete(row), stored.Replay ?? throw Incomplete(row));
    }

    private static SuggestionsTurn DecodeSuggestions(AssistantTurnRow row)
    {
        var stored = Deserialize<StoredSuggestions>(row);
        return new SuggestionsTurn(
            stored.Suggestions ?? throw Incomplete(row), stored.Replay ?? throw Incomplete(row));
    }

    private static InvalidOperationException Incomplete(AssistantTurnRow row) =>
        new($"Turn {row.Id} is missing a stored field.");

    private static T Deserialize<T>(AssistantTurnRow row) =>
        JsonSerializer.Deserialize<T>(row.Payload, Json)
            ?? throw new InvalidOperationException($"Turn {row.Id} has an empty payload.");

    private sealed record StoredQuestion(string Text, DateTimeOffset AskedAt);

    // QuickAnswerKind is stored as its number: append new kinds at the end, never reorder.
    private sealed record StoredQuickAnswer(QuickAnswer? Answer, string? Replay);

    private sealed record StoredSuggestions(QuickSuggestions? Suggestions, string? Replay);
}
