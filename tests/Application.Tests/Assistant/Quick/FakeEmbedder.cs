using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant.Quick;

namespace AiFramework.Application.Tests.Assistant.Quick;

/// <summary>
/// Hand-made vectors, so a test states exactly how similar two texts are. An unknown text is a
/// test bug, not a zero vector, so it throws.
/// </summary>
internal sealed class FakeEmbedder : ITextEmbedder
{
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);

    public bool Unavailable { get; set; }

    public FakeEmbedder Add(string text, params float[] vector)
    {
        _vectors[text] = VectorMath.Normalize([.. vector]);
        return this;
    }

    public Result<float[]> Embed(string text)
    {
        if (Unavailable)
        {
            return Result.Failure<float[]>(new Error(ErrorKind.Unavailable, "assistant.quick_unavailable", "Unavailable."));
        }

        if (!_vectors.TryGetValue(text, out var vector))
        {
            throw new InvalidOperationException($"FakeEmbedder has no vector for '{text}'.");
        }

        return Result.Success(vector);
    }
}
