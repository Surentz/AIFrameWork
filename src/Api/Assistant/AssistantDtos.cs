using AiFramework.Application.Assistant;
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

public sealed record AssistantStatusResponse
{
    public required bool Enabled { get; init; }
}

/// <summary>
/// One server-sent event's data. <see cref="Type"/> is also the SSE event name: text (a piece of
/// the answer), tool (a tool started), done (saved; carries the conversation id), error (carries a
/// ProblemDetails; nothing from this question was saved).
/// </summary>
public sealed record AssistantEventResponse
{
    public required string Type { get; init; }

    public string? Delta { get; init; }

    public string? Tool { get; init; }

    public Guid? ConversationId { get; init; }

    public AssistantOutcome? Outcome { get; init; }

    public ProblemDetails? Problem { get; init; }

    public static AssistantEventResponse Text(string delta) => new() { Type = "text", Delta = delta };

    public static AssistantEventResponse ToolStarted(string tool) => new() { Type = "tool", Tool = tool };

    public static AssistantEventResponse Done(AssistantAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return new() { Type = "done", ConversationId = answer.ConversationId, Outcome = answer.Outcome };
    }

    public static AssistantEventResponse Error(ProblemDetails problem) => new() { Type = "error", Problem = problem };
}
