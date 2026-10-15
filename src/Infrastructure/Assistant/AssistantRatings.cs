using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// Rates an answer through the change tracker; the unit of work commits it. Reads only the turns'
/// numbers and kinds, never their payloads, and writes nothing to the transcript.
/// </summary>
internal sealed class AssistantRatings(AiFrameworkDbContext context) : IAssistantRatings
{
    public async Task<RateResult> RateAsync(
        Guid conversationId, Guid ownerId, int turn, AnswerRating? rating, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var owned = await context.AssistantConversations
            .AsNoTracking()
            .AnyAsync(c => c.Id == conversationId && c.OwnerId == ownerId, cancellationToken)
            .ConfigureAwait(false);
        if (!owned)
        {
            return RateResult.NotFound;
        }

        var turns = await context.AssistantTurns
            .AsNoTracking()
            .Where(t => t.ConversationId == conversationId)
            .Select(t => new { t.Seq, t.Kind })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!turns.Any(t => t.Seq == turn))
        {
            return RateResult.NotFound;
        }

        if (!AnswerTurns.Of(turns.Select(t => (t.Seq, t.Kind))).Contains(turn))
        {
            return RateResult.NotAnAnswer;
        }

        var existing = await context.AssistantRatings
            .SingleOrDefaultAsync(r => r.ConversationId == conversationId && r.Turn == turn, cancellationToken)
            .ConfigureAwait(false);

        if (rating is not { } value)
        {
            if (existing is not null)
            {
                context.AssistantRatings.Remove(existing);
            }

            return RateResult.Rated;
        }

        if (existing is null)
        {
            context.AssistantRatings.Add(new AssistantRatingRow { ConversationId = conversationId, Turn = turn, Rating = value, RatedAt = at });
        }
        else
        {
            existing.Rating = value;
            existing.RatedAt = at;
        }

        return RateResult.Rated;
    }
}
