using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Assistant.Quick;

public enum QuickTargetKind
{
    Intent,
    Help,
}

/// <summary>What a phrase points at: an intent, or a help article (whose best section answers).</summary>
public sealed record QuickTarget(QuickTargetKind Kind, string Id);

public sealed record QuickMatch(QuickTarget Target, double Score, HelpSection? Section);

/// <summary>
/// Every example phrase and help section as a vector, in memory, searched by brute force. A few
/// hundred vectors take microseconds; this class is the one seam a vector database would replace
/// if the catalog ever grew into the tens of thousands (spec, "Growth"). ADR 0036.
/// </summary>
public sealed class PhraseIndex
{
    private readonly IReadOnlyList<Entry> _entries;

    private PhraseIndex(IReadOnlyList<Entry> entries) => _entries = entries;

    public int Count => _entries.Count;

    public static Result<PhraseIndex> Build(QuickCatalog catalog, ITextEmbedder embedder)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(embedder);

        var entries = new List<Entry>();
        foreach (var phrase in catalog.Phrases)
        {
            var intent = QuickIntents.Find(phrase.IntentId)
                ?? throw new InvalidOperationException($"Phrase for unknown intent '{phrase.IntentId}'.");
            var vector = embedder.Embed(phrase.Text);
            if (!vector.IsSuccess)
            {
                return Result.Failure<PhraseIndex>(vector.Error);
            }

            entries.Add(new Entry(new QuickTarget(QuickTargetKind.Intent, intent.Id), intent, null, vector.Value));
        }

        foreach (var article in catalog.Help.GroupBy(s => s.ArticleId, StringComparer.Ordinal))
        {
            var first = article.First();
            var texts = new List<(string Text, HelpSection Section)> { (first.Question, first) };
            texts.AddRange(article.Select(s => ($"{s.Heading}. {s.Text}", s)));

            foreach (var (text, section) in texts)
            {
                var vector = embedder.Embed(text);
                if (!vector.IsSuccess)
                {
                    return Result.Failure<PhraseIndex>(vector.Error);
                }

                entries.Add(new Entry(new QuickTarget(QuickTargetKind.Help, article.Key), null, section, vector.Value));
            }
        }

        return Result.Success(new PhraseIndex(entries));
    }

    /// <summary>The best score per target the scope may see, best first. Help is open to every scope.</summary>
    public IReadOnlyList<QuickMatch> Search(float[] query, ToolScope scope)
    {
        ArgumentNullException.ThrowIfNull(query);

        var best = new Dictionary<QuickTarget, QuickMatch>();
        foreach (var entry in _entries)
        {
            if (entry.Intent is { } intent && !QuickIntents.Allows(scope, intent))
            {
                continue;
            }

            var score = VectorMath.Dot(query, entry.Vector);
            if (!best.TryGetValue(entry.Target, out var current) || score > current.Score)
            {
                best[entry.Target] = new QuickMatch(entry.Target, score, entry.Section);
            }
        }

        return [.. best.Values.OrderByDescending(m => m.Score)];
    }

    private sealed record Entry(QuickTarget Target, QuickIntent? Intent, HelpSection? Section, float[] Vector);
}
