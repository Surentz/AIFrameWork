using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Assistant;

public sealed record InsightsDay(DateOnly Day, int Instant, int Help, int Suggested, int Ai);

public sealed record InsightsTarget(AnswerPath Path, string TargetId, int Questions, int Up, int Down);

/// <summary>A question the assistant missed: it ended in suggestions or the AI, or was rated 👎.</summary>
public sealed record InsightsMiss(DateTimeOffset At, AnswerPath Path, string? TargetId, double? BestScore, AnswerRating? Rating, string Question);

public sealed record AssistantInsightsView(
    DateTimeOffset Since, IReadOnlyList<InsightsDay> Days, IReadOnlyList<InsightsTarget> Targets, IReadOnlyList<InsightsMiss> Misses);

/// <summary>
/// Reads the outcome log. Misses carry the question text read live from the transcript, so a
/// deleted or expired conversation's question is gone with it; nothing here stores a copy.
/// </summary>
public interface IAssistantInsightsReader
{
    public Task<IReadOnlyList<InsightsDay>> DaysAsync(DateTimeOffset since, CancellationToken cancellationToken);

    public Task<IReadOnlyList<InsightsTarget>> TargetsAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// At most <paramref name="limit"/> misses: rated down first, then newest, so volume of AI and suggestion
    /// misses never pushes a thumbs-down out. The question text is returned as stored; the caller scrubs it.
    /// </summary>
    public Task<IReadOnlyList<InsightsMiss>> MissesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken);
}

/// <summary>For administrators; the API gates it with Monitoring.Read. ADR 0037.</summary>
public sealed record GetAssistantInsights(int Days) : IQuery<AssistantInsightsView>;

public sealed class GetAssistantInsightsHandler(IAssistantInsightsReader reader, IClock clock)
    : IQueryHandler<GetAssistantInsights, AssistantInsightsView>
{
    /// <summary>Conversations, and with them their outcomes, are kept 30 days.</summary>
    public const int MaxDays = 30;

    public const int MaxTargets = 20;

    public const int MaxMisses = 50;

    public async Task<Result<AssistantInsightsView>> HandleAsync(GetAssistantInsights query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Days is < 1 or > MaxDays)
        {
            return Result.Failure<AssistantInsightsView>(new Error(
                ErrorKind.Validation, "assistant.insights_window", $"The window is 1 to {MaxDays} days."));
        }

        var since = clock.UtcNow.AddDays(-query.Days);
        var days = await reader.DaysAsync(since, cancellationToken).ConfigureAwait(false);
        var targets = await reader.TargetsAsync(since, MaxTargets, cancellationToken).ConfigureAwait(false);
        var misses = await reader.MissesAsync(since, MaxMisses, cancellationToken).ConfigureAwait(false);

        // A member's own words, read by an administrator: never shown unscrubbed (ADR 0037).
        return Result.Success(new AssistantInsightsView(
            since,
            days,
            targets,
            [.. misses.Select(static m => m with { Question = OperationalText.Scrub(m.Question) ?? string.Empty })]));
    }
}
