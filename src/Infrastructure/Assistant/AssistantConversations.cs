using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// Transcripts through the change tracker, so the unit of work commits one question as one
/// transaction (never SaveChanges here). The conversation row is loaded tracked on purpose: its
/// xmin is what the commit checks.
/// </summary>
internal sealed class AssistantConversations(AiFrameworkDbContext context) : IAssistantConversations
{
    public async Task<AssistantConversation?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await context.AssistantConversations
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
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

        return new AssistantConversation(row.Id, row.OwnerId, row.QuestionCount, [.. turns.Select(AssistantTurnCodec.Decode)]);
    }

    public void Start(Guid id, Guid ownerId, string title, DateTimeOffset at) =>
        context.AssistantConversations.Add(new AssistantConversationRow
        {
            Id = id,
            OwnerId = ownerId,
            Title = title,
            CreatedAt = at,
            LastActiveAt = at,
        });

    public void Append(Guid id, IReadOnlyList<AssistantTurn> turns, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var row = context.AssistantConversations.Local.SingleOrDefault(c => c.Id == id)
            ?? throw new InvalidOperationException($"Conversation {id} must be found or started in this scope before it is appended to.");

        foreach (var turn in turns)
        {
            context.AssistantTurns.Add(AssistantTurnCodec.Encode(id, row.TurnCount, turn, at));
            row.TurnCount++;
        }

        row.QuestionCount++;
        row.LastActiveAt = at;
    }
}
