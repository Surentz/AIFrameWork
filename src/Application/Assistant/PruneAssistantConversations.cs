using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Assistant;

/// <summary>
/// Deletes assistant conversations idle for a day. The questions in them are free text an
/// administrator typed, so this bounds how long the application keeps them: a requirement of the
/// feature, not housekeeping. ADR 0033.
/// </summary>
public sealed record PruneAssistantConversations : IJob
{
    public static JobLane Lane => JobLane.Light;
}

public sealed class PruneAssistantConversationsHandler(IAssistantConversationRetention retention)
{
    public Task Handle(PruneAssistantConversations job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        return retention.PruneAsync(cancellationToken);
    }
}
