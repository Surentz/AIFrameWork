using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Assistant;

/// <summary>
/// A question. No length annotation: the 2,000-character rule belongs to AskAssistantValidator,
/// and duplicating it here would pre-empt that validator with a differently shaped 400.
/// </summary>
public sealed record AskAssistantRequest
{
    public Guid? ConversationId { get; init; }

    public required string Question { get; init; }
}

/// <summary>Which halves of the assistant are on: instant answers, and the AI.</summary>
public sealed record AssistantStatusResponse
{
    public required bool Quick { get; init; }

    public required bool Ai { get; init; }
}

public sealed record AssistantFactResponse
{
    public required string Label { get; init; }

    public required string Value { get; init; }
}

public sealed record AssistantTableResponse
{
    public required IReadOnlyList<string> Columns { get; init; }

    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }
}

/// <summary>An in-app path. The page renders it as a link only if it matches its allow-list.</summary>
public sealed record AssistantLinkResponse
{
    public required string Label { get; init; }

    public required string To { get; init; }
}

/// <summary>An instant or help answer, already formatted. Rendered as a card, never as Markdown.</summary>
public sealed record AssistantAnswerResponse
{
    public required QuickAnswerKind Kind { get; init; }

    public string? Title { get; init; }

    public required string Text { get; init; }

    public string? AnsweredAs { get; init; }

    public required IReadOnlyList<AssistantFactResponse> Facts { get; init; }

    public AssistantTableResponse? Table { get; init; }

    public AssistantLinkResponse? Link { get; init; }
}

/// <summary>What to offer when the router was not sure. AiAvailable says whether Ask the AI may be shown.</summary>
public sealed record AssistantSuggestionsResponse
{
    public required string Text { get; init; }

    public required IReadOnlyList<string> Options { get; init; }

    public required bool AiAvailable { get; init; }
}

/// <summary>
/// One server-sent event's data. <see cref="Type"/> is also the SSE event name: text (a piece of
/// the answer), tool (a tool started), answer (an instant or help answer, whole),
/// suggestions (what to offer when the router was not sure), done (saved; carries the conversation id), error (carries a
/// ProblemDetails; nothing from this question was saved). Answer, suggestions and done carry the answer's turn.
/// </summary>
public sealed record AssistantEventResponse
{
    public required string Type { get; init; }

    public string? Delta { get; init; }

    public string? Tool { get; init; }

    public Guid? ConversationId { get; init; }

    public AssistantOutcome? Outcome { get; init; }

    public ProblemDetails? Problem { get; init; }

    public AssistantAnswerResponse? Answer { get; init; }

    public AssistantSuggestionsResponse? Suggestions { get; init; }

    /// <summary>The stored turn that carries this answer: what a rating names. On answer, suggestions and done.</summary>
    public int? Turn { get; init; }

    public static AssistantEventResponse Answered(AssistantAnswerResponse answer, int turn) => new() { Type = "answer", Answer = answer, Turn = turn };

    public static AssistantEventResponse Suggested(AssistantSuggestionsResponse suggestions, int turn) => new() { Type = "suggestions", Suggestions = suggestions, Turn = turn };

    public static AssistantEventResponse Text(string delta) => new() { Type = "text", Delta = delta };

    public static AssistantEventResponse ToolStarted(string tool) => new() { Type = "tool", Tool = tool };

    public static AssistantEventResponse Done(AssistantAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return new() { Type = "done", ConversationId = answer.ConversationId, Outcome = answer.Outcome, Turn = answer.Turn };
    }

    public static AssistantEventResponse Error(ProblemDetails problem) => new() { Type = "error", Problem = problem };
}

public sealed record AssistantConversationSummaryResponse
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required DateTimeOffset LastActiveAt { get; init; }
}

/// <summary>One message as the page shows it. Role is user or assistant; Tools are the tool names the answer used.</summary>
public sealed record AssistantMessageResponse
{
    public required string Role { get; init; }

    public required string Text { get; init; }

    public required IReadOnlyList<string> Tools { get; init; }

    /// <summary>A quick answer's card, when this message is one.</summary>
    public AssistantAnswerResponse? Answer { get; init; }

    /// <summary>The suggestions, when this message is a not-sure reply.</summary>
    public AssistantSuggestionsResponse? Suggestions { get; init; }

    /// <summary>The turn an answer is rated on; null on a question.</summary>
    public int? Turn { get; init; }

    /// <summary>The owner's rating of this answer, if any.</summary>
    public AnswerRating? Rating { get; init; }
}

public sealed record AssistantConversationResponse
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required IReadOnlyList<AssistantMessageResponse> Messages { get; init; }
}

/// <summary>
/// A new title. No length annotation, for the reason given on AskAssistantRequest: the rule belongs
/// to RenameAssistantConversationValidator.
/// </summary>
public sealed record RenameAssistantConversationRequest
{
    public required string Title { get; init; }
}

/// <summary>A rating for one answer: Up, Down, or null to clear it.</summary>
public sealed record RateAnswerRequest
{
    public AnswerRating? Rating { get; init; }
}
