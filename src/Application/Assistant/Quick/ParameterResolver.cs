using System.Globalization;
using System.Text.RegularExpressions;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Statistics;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>An area, or the candidates when two names were too close to call, or neither.</summary>
public sealed record AreaResolution(PopulationAreaView? Area, IReadOnlyList<PopulationAreaView> Candidates);

/// <summary>
/// Reads an intent's parameters from the question with rules, never generation: a value it cannot
/// resolve is reported as missing, and the caller asks instead of guessing. ADR 0036.
/// </summary>
public sealed partial class ParameterResolver(IQueryDispatcher queries)
{
    public const double AreaMatch = 0.5;

    public const double AreaLead = 0.1;

    public static Guid? OrderId(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return FullGuid().Match(question) is { Success: true } match ? Guid.Parse(match.Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The first eight hex digits of an order id, as the order list shows them.</summary>
    public static string? OrderPrefix(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (FullGuid().IsMatch(question))
        {
            return null;
        }

        return ShortId().Match(question) is { Success: true } match ? match.Value.ToLowerInvariant() : null;
    }

    /// <summary>"6 hours", "3 days", "hour", "today", "week", or <paramref name="fallback"/>; at most the tools' 168 hours.</summary>
    public static TimeSpan Window(string question, TimeSpan fallback)
    {
        ArgumentNullException.ThrowIfNull(question);

        var lower = question.ToLowerInvariant();
        if (Hours().Match(lower) is { Success: true } hours)
        {
            return Clamp(int.Parse(hours.Groups["n"].Value, CultureInfo.InvariantCulture));
        }

        if (Days().Match(lower) is { Success: true } days)
        {
            return Clamp(int.Parse(days.Groups["n"].Value, CultureInfo.InvariantCulture) * 24);
        }

        if (lower.Contains("hour", StringComparison.Ordinal))
        {
            return TimeSpan.FromHours(1);
        }

        if (lower.Contains("week", StringComparison.Ordinal))
        {
            return TimeSpan.FromHours(AssistantToolbox.MaxWindowHours);
        }

        return lower.Contains("today", StringComparison.Ordinal) || lower.Contains("day", StringComparison.Ordinal)
            ? TimeSpan.FromHours(24)
            : fallback;
    }

    public async Task<Result<AreaResolution>> AreaAsync(string question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (AreaCode().Match(question) is { Success: true } code)
        {
            return Result.Success(new AreaResolution(new PopulationAreaView(code.Value, code.Value), []));
        }

        var areas = await queries.SendAsync(new GetPopulationAreas(), cancellationToken).ConfigureAwait(false);
        if (!areas.IsSuccess)
        {
            return Result.Failure<AreaResolution>(areas.Error);
        }

        var ranked = areas.Value.Areas
            .Select(area => (Area: area, Score: Fuzzy.WordSimilarity(area.Name, question)))
            .Where(r => r.Score >= AreaMatch)
            .OrderByDescending(r => r.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            return Result.Success(new AreaResolution(null, []));
        }

        if (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= AreaLead)
        {
            return Result.Success(new AreaResolution(ranked[0].Area, []));
        }

        return Result.Success(new AreaResolution(null, [.. ranked.Take(IntentRouter.MaxSuggestions).Select(r => r.Area)]));
    }

    private static TimeSpan Clamp(int hours) => TimeSpan.FromHours(Math.Clamp(hours, 1, AssistantToolbox.MaxWindowHours));

    [GeneratedRegex("\\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FullGuid();

    [GeneratedRegex("\\b[0-9a-fA-F]{8}\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ShortId();

    [GeneratedRegex("\\b(?<n>\\d{1,4})\\s*(?:hours?|hrs?|h)\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Hours();

    [GeneratedRegex("\\b(?<n>\\d{1,3})\\s*days?\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Days();

    [GeneratedRegex("\\b\\d{3}\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AreaCode();
}
