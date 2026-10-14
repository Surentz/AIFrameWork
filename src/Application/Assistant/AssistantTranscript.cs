using System.Text.Json;
using AiFramework.Application.Assistant.Quick;

namespace AiFramework.Application.Assistant;

/// <summary>One entry of a conversation's transcript, in the order the model saw it.</summary>
#pragma warning disable S2094 // A closed hierarchy's base: an abstract record keeps value equality on every turn type, which an interface would not
public abstract record AssistantTurn;
#pragma warning restore S2094

/// <summary>
/// What the administrator asked, and when. The time travels in this turn and never in the system
/// prompt, so the cached prompt prefix stays byte-identical across questions.
/// </summary>
public sealed record UserQuestionTurn(string Text, DateTimeOffset AskedAt) : AssistantTurn;

/// <summary>
/// One model response, exactly as the adapter stored it. Opaque here, deliberately: the provider
/// rejects a history whose earlier turns changed (preserved thinking), so Application never reads,
/// edits, trims or re-orders it. ADR 0033.
/// </summary>
public sealed record ModelTurn(string Blocks) : AssistantTurn;

/// <summary>The answer to every tool call of the model turn before it, as one user turn.</summary>
public sealed record ToolResultsTurn(IReadOnlyList<AssistantToolResult> Results) : AssistantTurn;

/// <summary>
/// An instant or help answer exactly as shown, and <paramref name="Replay"/>, the text Claude is
/// given for it if the user later asks the AI. Both are fixed when the turn is written: rendering
/// either again from a template would change history, which Claude rejects (ADR 0033, ADR 0036).
/// </summary>
public sealed record QuickAnswerTurn(QuickAnswer Answer, string Replay) : AssistantTurn;

/// <summary>The suggestions offered when the router was not sure, and their fixed replay text.</summary>
public sealed record SuggestionsTurn(QuickSuggestions Suggestions, string Replay) : AssistantTurn;

public sealed record AssistantToolCall(string Id, string Name, JsonElement Input);

public sealed record AssistantToolResult(string ToolCallId, string Content, bool IsError);

public enum AssistantStopReason
{
    EndTurn,
    ToolUse,
    MaxTokens,
    Refusal,
}

public sealed record AssistantUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens);

public sealed record AssistantModelTurn(
    ModelTurn Turn,
    IReadOnlyList<AssistantToolCall> ToolCalls,
    AssistantStopReason StopReason,
    AssistantUsage Usage);

/// <summary>A tool as the model is told about it. <paramref name="InputSchema"/> is a JSON Schema object.</summary>
public sealed record AssistantToolDefinition(string Name, string Description, string InputSchema);

/// <summary>Everything one model turn needs. <paramref name="AllowTools"/> false means tool_choice none.</summary>
public sealed record AssistantRequest(
    string SystemPrompt,
    IReadOnlyList<AssistantToolDefinition> Tools,
    IReadOnlyList<AssistantTurn> Transcript,
    bool AllowTools);
