using AiFramework.Application.Assistant;

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
}
