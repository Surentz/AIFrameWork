using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// Thirty days after the last question, one set-based delete; the turns go with their conversation
/// by ON DELETE CASCADE. Transcripts are personal data (ADR 0034).
/// </summary>
internal sealed class AssistantConversationRetention(AiFrameworkDbContext context, IClock clock)
    : IAssistantConversationRetention
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow - Retention;

        return context.AssistantConversations
            .Where(c => c.LastActiveAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
