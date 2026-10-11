using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>One set-based delete; the turns go with their conversation by ON DELETE CASCADE.</summary>
internal sealed class AssistantConversationRetention(AiFrameworkDbContext context, IClock clock)
    : IAssistantConversationRetention
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow - Retention;

        return context.AssistantConversations
            .Where(c => c.LastActiveAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
