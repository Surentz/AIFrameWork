using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant.Quick;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AiFramework.Infrastructure.Assistant.Quick;

/// <summary>
/// bge-small-en-v1.5 (quantised ONNX, MIT) through ONNX Runtime: WordPiece token ids in, the [CLS]
/// token's 384-number hidden state out, scaled to unit length. Loaded on first use, so a host that
/// never embeds (the worker) never loads it. <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>
/// is safe to call concurrently. ADR 0036.
/// <para>
/// A failed load is permanent for the process: <c>Lazy</c> caches the null, so every later call answers
/// Unavailable until the process restarts, even after the model files are fixed on disk.
/// </para>
/// <para>
/// One tokenizer instance is shared by every concurrent <see cref="Embed"/> call.
/// </para>
/// </summary>
public sealed class OnnxTextEmbedder(IOptions<QuickModelOptions> options) : ITextEmbedder, IDisposable
{
    /// <summary>The model's position limit. A longer question is cut; for a question that never matters.</summary>
    public const int MaxTokens = 512;

    private static readonly Error Unavailable = new(
        ErrorKind.Unavailable, "assistant.quick_unavailable", "Instant answers aren't available right now.");

    private readonly Lazy<Model?> _model = new(() => Load(options.Value.ModelPath), LazyThreadSafetyMode.ExecutionAndPublication);

    public Result<float[]> Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_model.Value is not { } model)
        {
            return Result.Failure<float[]>(Unavailable);
        }

        var ids = model.Tokenizer.EncodeToIds(text);
        var count = Math.Min(ids.Count, MaxTokens);
        var inputIds = new long[count];
        for (var i = 0; i < count; i++)
        {
            inputIds[i] = ids[i];
        }

        // A cut sequence still ends with [SEP], as the model was trained on.
        if (ids.Count > MaxTokens)
        {
            inputIds[^1] = ids[^1];
        }

        var mask = new long[count];
        Array.Fill(mask, 1L);
        var shape = new[] { 1, count };

        try
        {
            using var results = model.Session.Run(
            [
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, shape)),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, shape)),
                NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[count], shape)),
            ]);

            var hidden = results[0].AsTensor<float>();
            var vector = new float[hidden.Dimensions[2]];
            for (var d = 0; d < vector.Length; d++)
            {
                vector[d] = hidden[0, 0, d];
            }

            return Result.Success(VectorMath.Normalize(vector));
        }
        catch (OnnxRuntimeException)
        {
            return Result.Failure<float[]>(Unavailable);
        }
    }

    public void Dispose()
    {
        if (_model.IsValueCreated)
        {
            _model.Value?.Dispose();
        }
    }

    private static Model? Load(string directory)
    {
        var modelFile = Path.Combine(directory, "model.onnx");
        var vocabFile = Path.Combine(directory, "vocab.txt");
        if (!File.Exists(modelFile) || !File.Exists(vocabFile))
        {
            return null;
        }

        InferenceSession? session = null;
        try
        {
            session = OpenSession(modelFile);
            var tokenizer = OpenTokenizer(vocabFile);
            if (session is null || tokenizer is null)
            {
                return null;
            }

            var model = new Model(session, tokenizer);
            session = null; // the model owns the session now
            return model;
        }
        finally
        {
            session?.Dispose();
        }
    }

    // Returns null rather than throwing, so the caller can answer Unavailable. The model file is read
    // by ONNX Runtime, so an unreadable (locked, denied) file surfaces as IOException.
    private static InferenceSession? OpenSession(string modelFile)
    {
        try
        {
            return new InferenceSession(modelFile);
        }
        catch (OnnxRuntimeException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // BertTokenizer.Create reports a vocabulary it cannot use with ArgumentException (a missing special
    // token such as [UNK], or a duplicate entry) and reads the file with IOException. Measured against
    // Microsoft.ML.Tokenizers 2.0.0; it never throws OnnxRuntimeException.
    private static BertTokenizer? OpenTokenizer(string vocabFile)
    {
        try
        {
            return BertTokenizer.Create(vocabFile, new BertOptions { LowerCaseBeforeTokenization = true });
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Owns the session; OnnxTextEmbedder.Dispose reaches it through here.
    private sealed record Model(InferenceSession Session, BertTokenizer Tokenizer) : IDisposable
    {
        public void Dispose() => Session.Dispose();
    }
}
