using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// How sure the router must be. <see cref="Threshold"/>: the least similarity for an instant
/// answer. <see cref="Margin"/>: the least lead over the next target, so a close call between two
/// intents is a suggestion, never a guess. <see cref="SuggestionFloor"/>: below this a target is not
/// worth suggesting. Bound by the API from Assistant__Quick__*; the defaults come from the
/// evaluation gate (Task 13). ADR 0036.
/// </summary>
public sealed record QuickRouting(double Threshold, double Margin, double SuggestionFloor)
{
    public static QuickRouting Default { get; } = new(Threshold: 0.80, Margin: 0.04, SuggestionFloor: 0.55);
}

/// <summary>
/// How the router reached a decision, for the outcome log (PR 2): the best match's similarity,
/// its lead over the runner-up, and whether the spelling correction won. Never the question.
/// </summary>
public sealed record RouteEvidence(double BestScore, double Margin, bool Corrected)
{
    public static RouteEvidence None { get; } = new(0, 0, false);
}

public abstract record RouteDecision
{
    public RouteEvidence Evidence { get; init; } = RouteEvidence.None;
}

public sealed record RouteToIntent(QuickIntent Intent, double Score, string? AnsweredAs) : RouteDecision;

public sealed record RouteToHelp(HelpSection Section, double Score, string? AnsweredAs) : RouteDecision;

public sealed record RouteUnsure(IReadOnlyList<string> Suggestions) : RouteDecision;

public sealed class IntentRouter(QuickIndexState state, ITextEmbedder embedder, QuickRouting routing)
{
    public const int MaxSuggestions = 3;

    private static readonly Error NotReady = new(
        ErrorKind.Unavailable, "assistant.quick_unavailable", "Instant answers aren't available right now.");

    public Result<RouteDecision> Route(string question, ToolScope scope)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (!state.TryGet(out var index, out var spelling))
        {
            return Result.Failure<RouteDecision>(NotReady);
        }

        var original = embedder.Embed(question);
        if (!original.IsSuccess)
        {
            return Result.Failure<RouteDecision>(original.Error);
        }

        var (decision, score) = Decide(index.Search(original.Value, scope), answeredAs: null);

        // The correction is a second opinion, used only when it makes an instant match the
        // original question did not, or a better one.
        if (spelling.Correct(question) is { } corrected)
        {
            var embedded = embedder.Embed(corrected);
            if (embedded.IsSuccess)
            {
                var (alternative, alternativeScore) = Decide(index.Search(embedded.Value, scope), corrected);
                if (alternative is not RouteUnsure && (decision is RouteUnsure || alternativeScore > score))
                {
                    decision = alternative;
                }
            }
        }

        return Result.Success(decision);
    }

    private (RouteDecision Decision, double Score) Decide(IReadOnlyList<QuickMatch> matches, string? answeredAs)
    {
        if (matches.Count == 0)
        {
            return (new RouteUnsure([]) { Evidence = RouteEvidence.None with { Corrected = answeredAs is not null } }, 0);
        }

        var top = matches[0];
        var runnerUp = matches.Count > 1 ? matches[1].Score : 0;
        var evidence = new RouteEvidence(top.Score, top.Score - runnerUp, answeredAs is not null);
        if (top.Score >= routing.Threshold && top.Score - runnerUp >= routing.Margin)
        {
            RouteDecision decision = top.Target.Kind == QuickTargetKind.Intent
                ? new RouteToIntent(QuickIntents.Find(top.Target.Id) ?? throw new InvalidOperationException(top.Target.Id), top.Score, answeredAs)
                : new RouteToHelp(top.Section ?? throw new InvalidOperationException(top.Target.Id), top.Score, answeredAs);
            return (decision with { Evidence = evidence }, top.Score);
        }

        var suggestions = matches
            .Where(m => m.Score >= routing.SuggestionFloor)
            .Take(MaxSuggestions)
            .Select(m => m.Target.Kind == QuickTargetKind.Intent
                ? QuickIntents.Find(m.Target.Id)?.CanonicalQuestion
                : m.Section?.Question)
            .OfType<string>()
            .ToList();

        return (new RouteUnsure(suggestions) { Evidence = evidence }, top.Score);
    }
}
