using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// The worker's IAssistantOutput. The worker never asks the assistant anything, but the generic
/// host validates every registered descriptor at build time, and AskAssistantHandler is registered
/// in both hosts — the same reason NoClientContext exists.
/// </summary>
public sealed class NoAssistantOutput : IAssistantOutput
{
    public Task TextAsync(string delta, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ToolAsync(string toolName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AnswerAsync(QuickAnswer answer, int turn, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SuggestionsAsync(QuickSuggestions suggestions, int turn, CancellationToken cancellationToken) => Task.CompletedTask;
}
