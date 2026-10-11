using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Assistant;

/// <summary>
/// One model turn. Infrastructure implements it with the Anthropic SDK; nothing here knows that.
/// Text is pushed to the output as it arrives. Fails with <see cref="ErrorKind.Unavailable"/> when
/// the provider cannot be reached or rejects the request.
/// </summary>
public interface IAssistantModel
{
    public Task<Result<AssistantModelTurn>> NextTurnAsync(
        AssistantRequest request, IAssistantOutput output, CancellationToken cancellationToken);
}

/// <summary>
/// Where an answer goes while it is being written. The API writes server-sent events; the worker,
/// which never asks, registers a no-op. Registered per host, never in AddInfrastructure.
/// </summary>
public interface IAssistantOutput
{
    public Task TextAsync(string delta, CancellationToken cancellationToken);

    public Task ToolAsync(string toolName, CancellationToken cancellationToken);
}

public sealed record AssistantConversation(
    Guid Id, Guid OwnerId, int QuestionCount, IReadOnlyList<AssistantTurn> Turns);

/// <summary>
/// The transcripts. Writes are tracked and committed by the unit of work, so one question is one
/// transaction: a failed question leaves nothing behind.
/// </summary>
public interface IAssistantConversations
{
    /// <summary>Loads and tracks the conversation, so the commit checks its row version.</summary>
    public Task<AssistantConversation?> FindAsync(Guid id, CancellationToken cancellationToken);

    public void Start(Guid id, Guid ownerId, string title, DateTimeOffset at);

    /// <summary>Adds one question's turns after the existing ones and counts the question.</summary>
    public void Append(Guid id, IReadOnlyList<AssistantTurn> turns, DateTimeOffset at);
}

/// <summary>Deletes conversations idle for longer than the retention. ADR 0033.</summary>
public interface IAssistantConversationRetention
{
    public Task<int> PruneAsync(CancellationToken cancellationToken);
}
