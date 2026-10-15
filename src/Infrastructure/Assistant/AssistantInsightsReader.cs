using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Assistant;

internal sealed class AssistantInsightsReader(AiFrameworkDbContext context) : IAssistantInsightsReader
{
    public async Task<IReadOnlyList<InsightsDay>> DaysAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var rows = await context.Database
            .SqlQuery<DayPathCount>($"""
                SELECT (o."At" AT TIME ZONE 'UTC')::date AS "Day", o."Path" AS "Path", count(*)::int AS "Total"
                FROM assistant_outcomes o
                WHERE o."At" >= {since}
                GROUP BY 1, 2
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows
            .GroupBy(static r => r.Day)
            .OrderBy(static g => g.Key)
            .Select(static g => new InsightsDay(
                g.Key,
                Count(g, AnswerPath.Instant),
                Count(g, AnswerPath.Help),
                Count(g, AnswerPath.Suggested),
                Count(g, AnswerPath.Ai)))];
    }

    public async Task<IReadOnlyList<InsightsTarget>> TargetsAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken)
    {
        var rows = await context.Database
            .SqlQuery<TargetCount>($"""
                SELECT o."Path" AS "Path", o."TargetId" AS "TargetId", count(*)::int AS "Questions",
                       count(*) FILTER (WHERE r."Rating" = 'Up')::int AS "Up",
                       count(*) FILTER (WHERE r."Rating" = 'Down')::int AS "Down"
                FROM assistant_outcomes o
                LEFT JOIN assistant_ratings r ON r."ConversationId" = o."ConversationId" AND r."Turn" = o."AnswerTurn"
                WHERE o."At" >= {since} AND o."TargetId" IS NOT NULL
                GROUP BY o."Path", o."TargetId"
                ORDER BY count(*) DESC, o."TargetId"
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(static r => new InsightsTarget(Enum.Parse<AnswerPath>(r.Path), r.TargetId, r.Questions, r.Up, r.Down))];
    }

    public async Task<IReadOnlyList<InsightsMiss>> MissesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken)
    {
        var missed = await (
                from o in context.AssistantOutcomes.AsNoTracking()
                join r in context.AssistantRatings.AsNoTracking()
                    on new { o.ConversationId, Turn = o.AnswerTurn } equals new { r.ConversationId, r.Turn } into rated
                from r in rated.DefaultIfEmpty()
                where o.At >= since
                    && (o.Path == AnswerPath.Suggested || o.Path == AnswerPath.Ai || (r != null && r.Rating == AnswerRating.Down))
                orderby o.At descending
                select new { o.ConversationId, o.QuestionTurn, o.At, o.Path, o.TargetId, o.BestScore, Rating = r == null ? (AnswerRating?)null : r.Rating })
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var conversationIds = missed.Select(static m => m.ConversationId).Distinct().ToList();
        var questions = await context.AssistantTurns
            .AsNoTracking()
            .Where(t => conversationIds.Contains(t.ConversationId) && t.Kind == AssistantTurnCodec.Question)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var text = questions.ToDictionary(
            static t => (t.ConversationId, t.Seq),
            static t => AssistantTurnCodec.Decode(t) is UserQuestionTurn q ? q.Text : string.Empty);

        return [.. missed.Select(m => new InsightsMiss(
            m.At, m.Path, m.TargetId, m.BestScore, m.Rating,
            text.GetValueOrDefault((m.ConversationId, m.QuestionTurn), string.Empty)))];
    }

    private static int Count(IEnumerable<DayPathCount> day, AnswerPath path) =>
        day.Where(r => string.Equals(r.Path, path.ToString(), StringComparison.Ordinal)).Sum(static r => r.Total);

    private sealed record DayPathCount(DateOnly Day, string Path, int Total);

    private sealed record TargetCount(string Path, string TargetId, int Questions, int Up, int Down);
}
