using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// The sidebar's reads and the owner's rename and delete, always filtered by owner. Writes go
/// through the change tracker for the unit of work; the tracked row's xmin makes a rename or delete
/// that races a question in the same conversation a 409 for one of them.
/// </summary>
internal sealed class AssistantHistoryStore(AiFrameworkDbContext context) : IAssistantHistory
{
    public async Task<IReadOnlyList<AssistantConversationSummary>> ListAsync(Guid ownerId, CancellationToken cancellationToken) =>
        await context.AssistantConversations
            .AsNoTracking()
            .Where(c => c.OwnerId == ownerId)
            .OrderByDescending(c => c.LastActiveAt)
            .Select(c => new AssistantConversationSummary(c.Id, c.Title, c.LastActiveAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<AssistantConversationView?> ReadAsync(Guid id, Guid ownerId, CancellationToken cancellationToken)
    {
        var row = await context.AssistantConversations
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id && c.OwnerId == ownerId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var turns = await context.AssistantTurns
            .AsNoTracking()
            .Where(t => t.ConversationId == id)
            .OrderBy(t => t.Seq)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ratings = await context.AssistantRatings
            .AsNoTracking()
            .Where(r => r.ConversationId == id)
            .ToDictionaryAsync(r => r.Turn, r => r.Rating, cancellationToken)
            .ConfigureAwait(false);

        return new AssistantConversationView(row.Id, row.Title, AssistantDisplay.Build(turns, ratings));
    }

    public async Task<bool> RenameAsync(Guid id, Guid ownerId, string title, CancellationToken cancellationToken)
    {
        var row = await FindAsync(id, ownerId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        row.Title = title;
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid ownerId, CancellationToken cancellationToken)
    {
        var row = await FindAsync(id, ownerId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        // The turns go by ON DELETE CASCADE when the unit of work commits.
        context.AssistantConversations.Remove(row);
        return true;
    }

    private Task<AssistantConversationRow?> FindAsync(Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        context.AssistantConversations.SingleOrDefaultAsync(c => c.Id == id && c.OwnerId == ownerId, cancellationToken);
}
