using System.Text;
using System.Text.RegularExpressions;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// pg_trgm's word similarity, in memory, for short lists such as population areas: the best
/// trigram overlap between a name and any run of the text's words of about the name's length.
/// </summary>
public static partial class Fuzzy
{
    public static double WordSimilarity(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(text);

        var nameWords = Words(Fold(name));
        var textWords = Words(Fold(text));
        if (nameWords.Count == 0 || textWords.Count == 0)
        {
            return 0;
        }

        var target = Trigrams(nameWords);
        var best = 0d;
        for (var size = Math.Max(1, nameWords.Count - 1); size <= nameWords.Count + 1; size++)
        {
            for (var start = 0; start + size <= textWords.Count; start++)
            {
                best = Math.Max(best, Jaccard(target, Trigrams(textWords.Skip(start).Take(size))));
            }
        }

        return best;
    }

    /// <summary>Lower case, with the Danish letters spelt out. No Unicode normalisation: the runtime image has no ICU.</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var folded = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            folded.Append(c switch
            {
                'æ' => "ae",
                'ø' => "o",
                'å' => "aa",
                'é' or 'è' => "e",
                'ü' => "u",
                'ö' => "o",
                'ä' => "a",
                _ => c.ToString(),
            });
        }

        return folded.ToString();
    }

    private static List<string> Words(string text) => [.. Word().Matches(text).Select(m => m.Value)];

    private static HashSet<string> Trigrams(IEnumerable<string> words)
    {
        var trigrams = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            var padded = $"  {word} ";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                trigrams.Add(padded.Substring(i, 3));
            }
        }

        return trigrams;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    [GeneratedRegex("[a-z0-9]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Word();
}
