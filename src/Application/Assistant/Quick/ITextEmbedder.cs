using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// Turns text into a unit-length vector, so the dot product of two is their cosine similarity.
/// Infrastructure runs a local model (ADR 0036): nothing leaves the process. Fails with
/// <see cref="ErrorKind.Unavailable"/> when the model is missing or cannot run.
/// </summary>
public interface ITextEmbedder
{
    public Result<float[]> Embed(string text);
}
