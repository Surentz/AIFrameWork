using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Infrastructure.Assistant.Quick;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Assistant.Quick;

public sealed class OnnxTextEmbedderTests : IDisposable
{
    private readonly OnnxTextEmbedder _embedder = new(Options.Create(new QuickModelOptions()));

    public void Dispose() => _embedder.Dispose();

    [Fact]
    public void TheModelHasBeenFetched()
    {
        // Not a skip: a missing model is a red test that says how to fix it.
        File.Exists(Path.Combine(new QuickModelOptions().ModelPath, "model.onnx")).Should().BeTrue(
            "the assistant model must be fetched before building: run scripts/fetch-assistant-model.ps1");
    }

    [Fact]
    public void Embed_ReturnsAUnitVectorOf384Numbers()
    {
        var vector = _embedder.Embed("Has my last order shipped?").Value;

        vector.Should().HaveCount(384);
        VectorMath.Dot(vector, vector).Should().BeApproximately(1, 1e-4);
    }

    [Fact]
    public void Embed_IsDeterministic()
    {
        _embedder.Embed("Is my export ready?").Value.Should().Equal(_embedder.Embed("Is my export ready?").Value);
    }

    [Fact]
    public void Embed_PutsAParaphraseCloserThanAnUnrelatedQuestion()
    {
        var question = _embedder.Embed("Has my last order shipped?").Value;
        var paraphrase = _embedder.Embed("Where is my latest order?").Value;
        var unrelated = _embedder.Embed("How much is the Widget?").Value;

        VectorMath.Dot(question, paraphrase).Should().BeGreaterThan(VectorMath.Dot(question, unrelated) + 0.2);
    }

    [Fact]
    public void Embed_CutsAVeryLongQuestionInsteadOfFailing()
    {
        var longQuestion = string.Join(' ', Enumerable.Repeat("order", 3000));

        _embedder.Embed(longQuestion).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Embed_WithNoModel_IsUnavailable()
    {
        using var missing = new OnnxTextEmbedder(Options.Create(new QuickModelOptions
        {
            ModelPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        }));

        var result = missing.Embed("anything");

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
        result.Error.Code.Should().Be("assistant.quick_unavailable");
    }
}
