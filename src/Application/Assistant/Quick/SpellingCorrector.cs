namespace AiFramework.Application.Assistant.Quick;

/// <summary>Corrects misspelt words against the catalog's own vocabulary. Task 5 gives it its behaviour.</summary>
public sealed class SpellingCorrector(IEnumerable<string> texts)
{
    private readonly IReadOnlyList<string> _texts = [.. texts];

    public int VocabularySize => _texts.Count;

    /// <summary>The corrected question, or null when nothing was changed.</summary>
#pragma warning disable CA1822, S2325 // Pass-through until Task 5: the vocabulary (_texts) is what the correction will read, so it stays an instance method.
    public string? Correct(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return null;
    }
#pragma warning restore CA1822, S2325
}
