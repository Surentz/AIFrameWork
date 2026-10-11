using System.Text.Json;
using AiFramework.Application.Assistant;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>The one mapping between a transcript turn and its stored row, both ways.</summary>
internal static class AssistantTurnCodec
{
    public const string Question = "question";
    public const string Model = "model";
    public const string ToolResults = "tool_results";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AssistantTurnRow Encode(Guid conversationId, int seq, AssistantTurn turn, DateTimeOffset at)
    {
        var (kind, payload) = turn switch
        {
            UserQuestionTurn q => (Question, JsonSerializer.Serialize(new StoredQuestion(q.Text, q.AskedAt), Json)),
            ModelTurn m => (Model, m.Blocks),
            ToolResultsTurn r => (ToolResults, JsonSerializer.Serialize(r.Results, Json)),
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
        _ => throw new InvalidOperationException($"Turn {row.Id} has unknown kind '{row.Kind}'."),
    };

    private static UserQuestionTurn DecodeQuestion(AssistantTurnRow row)
    {
        var question = Deserialize<StoredQuestion>(row);
        return new UserQuestionTurn(question.Text, question.AskedAt);
    }

    private static T Deserialize<T>(AssistantTurnRow row) =>
        JsonSerializer.Deserialize<T>(row.Payload, Json)
            ?? throw new InvalidOperationException($"Turn {row.Id} has an empty payload.");

    private sealed record StoredQuestion(string Text, DateTimeOffset AskedAt);
}
