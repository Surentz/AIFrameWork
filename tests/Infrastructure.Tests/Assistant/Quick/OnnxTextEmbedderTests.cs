using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Infrastructure.Assistant.Quick;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Assistant.Quick;

public sealed class OnnxTextEmbedderTests : IDisposable
{
    private readonly OnnxTextEmbedder _embedder = new(Options.Create(new QuickModelOptions()), NullLogger<OnnxTextEmbedder>.Instance);

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
        }), NullLogger<OnnxTextEmbedder>.Instance);

        var result = missing.Embed("anything");

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
        result.Error.Code.Should().Be("assistant.quick_unavailable");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a vocabulary\nat all\n")]
    [InlineData("[PAD]\n[CLS]\n[SEP]\n")]
    [InlineData("[PAD]\n[UNK]\n[CLS]\n[SEP]\n[PAD]\n")]
    public void Embed_WithABrokenVocabulary_IsUnavailableEveryTime(string vocabulary)
    {
        var directory = NewModelDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "model.onnx"), [0x08, 0x01]);
            File.WriteAllText(Path.Combine(directory, "vocab.txt"), vocabulary);
            using var embedder = new OnnxTextEmbedder(Options.Create(new QuickModelOptions { ModelPath = directory }), NullLogger<OnnxTextEmbedder>.Instance);

            AssertUnavailable(embedder.Embed("first question"));
            AssertUnavailable(embedder.Embed("second question"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Embed_WithACorruptModel_IsUnavailableEveryTime()
    {
        var directory = NewModelDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "model.onnx"), [0x08, 0x01]);
            File.Copy(Path.Combine(new QuickModelOptions().ModelPath, "vocab.txt"), Path.Combine(directory, "vocab.txt"));
            using var embedder = new OnnxTextEmbedder(Options.Create(new QuickModelOptions { ModelPath = directory }), NullLogger<OnnxTextEmbedder>.Instance);

            AssertUnavailable(embedder.Embed("first question"));
            AssertUnavailable(embedder.Embed("second question"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(NativeLoadFailures))]
    public void Embed_WhenTheNativeRuntimeCannotLoad_IsUnavailableEveryTime(Exception failure)
    {
        // A missing or mismatched onnxruntime library surfaces from the session constructor; the real
        // one is already loaded in this process, so the test fails the constructor instead.
        using var embedder = new OnnxTextEmbedder(
            Options.Create(new QuickModelOptions()), NullLogger<OnnxTextEmbedder>.Instance, _ => throw failure);

        AssertUnavailable(embedder.Embed("first question"));
        AssertUnavailable(embedder.Embed("second question"));
    }

    public static TheoryData<Exception> NativeLoadFailures() =>
    [
        new DllNotFoundException("onnxruntime"),
        new EntryPointNotFoundException("OrtGetApiBase"),
        new BadImageFormatException("onnxruntime"),
        new TypeInitializationException("Microsoft.ML.OnnxRuntime.NativeMethods", new DllNotFoundException("onnxruntime")),
    ];

    private static string NewModelDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void AssertUnavailable(Result<float[]> result)
    {
        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
        result.Error.Code.Should().Be("assistant.quick_unavailable");
    }
}
