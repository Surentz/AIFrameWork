using System.Diagnostics.CodeAnalysis;
using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// The published index, once the API has built it at start-up. A singleton: until it is published
/// (or if building failed) the router reports itself unavailable. ADR 0036.
/// </summary>
public sealed class QuickIndexState
{
    private volatile Ready? _ready;

    public bool IsReady => _ready is not null;

    public void Publish(PhraseIndex index, SpellingCorrector spelling)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(spelling);
        _ready = new Ready(index, spelling);
    }

    public bool TryGet([NotNullWhen(true)] out PhraseIndex? index, [NotNullWhen(true)] out SpellingCorrector? spelling)
    {
        var ready = _ready;
        index = ready?.Index;
        spelling = ready?.Spelling;
        return ready is not null;
    }

    private sealed record Ready(PhraseIndex Index, SpellingCorrector Spelling);
}

public static class QuickIndexBuilder
{
    public static Result<(PhraseIndex Index, SpellingCorrector Spelling)> Build(QuickCatalog catalog, ITextEmbedder embedder)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var index = PhraseIndex.Build(catalog, embedder);
        if (!index.IsSuccess)
        {
            return Result.Failure<(PhraseIndex, SpellingCorrector)>(index.Error);
        }

        var vocabulary = catalog.Phrases.Select(p => p.Text)
            .Concat(QuickIntents.All.Select(i => i.CanonicalQuestion))
            .Concat(catalog.Help.SelectMany(s => new[] { s.Title, s.Question, s.Heading, s.Text }));

        return Result.Success((index.Value, new SpellingCorrector(vocabulary)));
    }
}
