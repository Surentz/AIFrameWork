using System.Text.RegularExpressions;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// Corrects misspelt words against the catalog's own vocabulary: a word of at least
/// <see cref="MinLength"/> letters that the catalog never uses becomes the one vocabulary word within
/// one edit (two for longer words), if there is exactly one. Ids, numbers and quoted text are never
/// touched, and the router uses a correction only when it produces an instant match. Brute force:
/// the vocabulary is a few hundred words. ADR 0036.
/// </summary>
public sealed partial class SpellingCorrector
{
    public const int MinLength = 4;

    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly string[] _vocabulary;

    public SpellingCorrector(IEnumerable<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        foreach (var text in texts)
        {
            foreach (Match word in Word().Matches(text))
            {
                _known.Add(word.Value.ToLowerInvariant());
            }
        }

        _vocabulary = [.. _known.Where(w => w.Length >= MinLength - 1).Order(StringComparer.Ordinal)];
    }

    public int VocabularySize => _known.Count;

    /// <summary>The corrected question, or null when nothing was changed.</summary>
    public string? Correct(string question)
    {
        ArgumentNullException.ThrowIfNull(question);

        var changed = false;
        var corrected = Token().Replace(question, match =>
        {
            if (match.Value.StartsWith('"'))
            {
                return match.Value;
            }

            var lower = match.Value.ToLowerInvariant();
            if (lower.Length < MinLength || _known.Contains(lower) || Closest(lower) is not { } fix)
            {
                return match.Value;
            }

            changed = true;
            return fix;
        });

        return changed ? corrected : null;
    }

    /// <summary>
    /// Optimal string alignment distance (insertions, deletions, substitutions and adjacent swaps),
    /// cut off at <paramref name="limit"/> + 1.
    /// </summary>
    public static int EditDistance(string a, string b, int limit)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return limit + 1;
        }

        var before = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMinimum = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    current[j] = Math.Min(current[j], before[j - 2] + 1);
                }

                rowMinimum = Math.Min(rowMinimum, current[j]);
            }

            if (rowMinimum > limit)
            {
                return limit + 1;
            }

            (before, previous, current) = (previous, current, before);
        }

        return Math.Min(previous[b.Length], limit + 1);
    }

    private string? Closest(string word)
    {
        var limit = word.Length <= 5 ? 1 : 2;
        string? best = null;
        var bestDistance = limit + 1;
        var tie = false;

        foreach (var candidate in _vocabulary)
        {
            var distance = EditDistance(word, candidate, limit);
            if (distance < bestDistance)
            {
                (best, bestDistance, tie) = (candidate, distance, false);
            }
            else if (distance == bestDistance && distance <= limit)
            {
                tie = true;
            }
        }

        return tie ? null : best;
    }

    /// <summary>A quoted run (kept verbatim) or a word of letters only, so "3f2a1b9c" and "SKU-1042" are not words.</summary>
    [GeneratedRegex("\"[^\"]*\"|\\b[A-Za-z]+\\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Token();

    [GeneratedRegex("[A-Za-z]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Word();
}
