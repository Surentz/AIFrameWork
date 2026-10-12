# Assistant instant answers (PR 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Answer application questions instantly and precisely, without a language model: a local embedding model recognises the question, the same caller-scoped queries the AI's tools use supply the figures, and the reply is a typed card with a link to the screen. Claude stays available on request.

**Architecture:** A new `Application/Assistant/Quick` slice: an `ITextEmbedder` port (ONNX Runtime adapter in Infrastructure, model files fetched and hash-checked, copied beside the API's binaries), an in-memory `PhraseIndex` built at API start-up from catalog files embedded in the Application assembly, a `SpellingCorrector`, an `IntentRouter` (threshold + margin), a `ParameterResolver`, and a `QuickAnswerComposer` that dispatches existing queries. A new `AskQuick` command answers through a new endpoint, `POST /api/assistant/quick`, streaming `answer` or `suggestions` events; replies are stored as two new append-only turn types that replay to Claude as plain text. The chat renders cards, suggestion chips and *Ask the AI*.

**Tech Stack:** .NET 10, ONNX Runtime 1.30.0, Microsoft.ML.Tokenizers 2.0.0, bge-small-en-v1.5 (quantised ONNX, MIT), EF Core 10 + Npgsql (`pg_trgm`), xUnit + FluentAssertions + NSubstitute + Testcontainers, React 19 + Vitest + MSW, Playwright.

**Spec:** `docs/superpowers/specs/2026-10-12-assistant-instant-answers-design.md`

## Global Constraints

- PR type `feat`, title `feat(assistant): instant answers before the AI`. Every commit subject `feat(assistant): …` (docs-only commits `docs(assistant): …`), every message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Branch `claude/assistant-instant-answers` (exists; spec committed). PR against `main` only.
- English only. Read-only: no command is ever dispatched by the quick path.
- Warnings are errors (compiler, analyzers). Nullable on. **Never `catch (Exception)`**; catch the specific type. `ConfigureAwait(false)` on every await in `src/`.
- Application references **no new package**: no ONNX, no Tokenizers, no `System.Numerics.Tensors`, no `Microsoft.Extensions.Options`. Vector maths is plain loops.
- Api depends on Infrastructure **for DI only** (`ArchitectureTests`); controllers and DTO mappers never name an Infrastructure type.
- Per-host registration: the start-up index build (`AddQuickAnswerIndexing`) is called **only** by `src/Api/Program.cs`; the worker must never load the model.
- DTOs use `required` + `init`. Changing a controller or DTO means regenerating `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` (the `regenerate` skill).
- Transcripts are append-only (ADR 0033): a stored turn is never rewritten or re-rendered; quick turns store their replay text at write time.
- Never log a question or a request instance (`Behaviors.LoggedAsync` already logs the command).
- Migrations: generated with `dotnet ef` (`src/Infrastructure` is both project and startup project, `ConnectionStrings__Default` in the environment), never hand-edited once committed.
- Numbers and dates in answers are formatted with `CultureInfo.InvariantCulture` (the chiseled runtime image has no ICU): dates as `d MMM yyyy, HH:mm 'UTC'`, prices `0.00`, counts `N0`.
- Frontend: function components, explicit prop interfaces and return types, no `any`, no `!`, `eslint --max-warnings 0`. Answers render from structured data, never Markdown or HTML; links only through the allow-list.
- E2E: import `test`/`expect` from `../../fixtures/index.ts`; role/label locators only; assertions only in specs; every new spec that needs the fake Messages API or quick answers is `@local-only`; no new auth permit is spent.
- Run backend commands from the repo root, frontend commands from `frontend/`.

## Rulings that differ from the spec (for review before execution)

1. **A separate endpoint instead of a `mode` field.** The rate limiter runs before model binding and cannot read the body, so quick and AI questions could not get separate limits. Quick: `POST /api/assistant/quick` (new policy, 120/hour). AI: `POST /api/assistant/messages`, unchanged (404 when the AI is off). The spec's `assistant.ai_disabled` 400 is therefore not needed.
2. **No automatic fall-through to the AI.** When the router is unavailable, the quick endpoint replies "Instant answers aren't available right now" with *Ask the AI*; it never calls Claude itself, so a 120/hour quick budget can never spend the 20/hour AI budget.
3. **The composer dispatches the queries directly** (`GetOrder`, `GetOrders`, `GetOrderExports`, …) instead of parsing `AssistantToolbox`'s JSON. Same queries, same per-caller scoping; the `ToolResults` projections stay what they are for — what leaves for Anthropic.
4. **Spelling: an in-house edit-distance corrector over the catalog's own vocabulary**, not the SymSpell package plus a common-English list. The vocabulary is a few hundred words, so a brute-force search is microseconds; no word list of unclear licence is shipped; and a correction is used only when it produces an instant match. The probe measured 0.969 similarity for "has my last order shiped" without any correction, so this pass is a safety net, not the main typo defence.
5. **Product search uses `pg_trgm`'s `word_similarity`** (it finds a product's name *inside* a question) through raw SQL. The migration enables the extension; no trigram index is added, because the function form cannot use one and the catalogue is small. Revisit past roughly 10,000 products.
6. **Model: bge-small-en-v1.5, quantised (34 MB, MIT)**, Hugging Face commit `ea104dacec62c0de699686887e3f920caeb4f3e3`. A probe on this machine: 1.7 ms per question, 384 dimensions, CLS pooling. MiniLM was not evaluated; the evaluation gate decides whether this one is good enough.
7. **The index is built while the API starts** (an `IHostedService` whose `StartAsync` awaits the build, about a second), not lazily: a started API either answers instantly or knows it cannot. Readiness is unaffected either way.
8. **Help articles:** the spec's "cancelling an order" is replaced by "signing out everywhere". Members have no cancel button in the UI, so that article would describe something that does not exist.
9. Failed-job tables show job name, attempt and start time only, **never error text**, so nothing free-form reaches a replay.

## Review Focus

1. **A member asks an operator question** ("did any job fail today?"): the reply must never contain monitoring data, instantly or as a suggestion. Pinned in Task 4 (router scope test) and Task 11 (API test).
2. **Ask the AI after quick replies:** the Claude request must alternate roles and carry each quick turn's stored replay text byte for byte. Pinned in Task 8 (mapper test) and Task 15 (e2e Ask the AI path).
3. **A wrong instant answer** for any labelled question, typos included: the evaluation gate in Task 13 fails CI.
4. **The model in the chiseled runtime image** (native `libonnxruntime.so` dependencies) is exercised by no unit test: Task 16 deploys to kind and asks a quick question; if kind is unavailable, the PR says the image is unverified.
5. **Two similar products or areas:** the reply must ask "Did you mean…", never pick one. Pinned in Task 7 (composer tests).

## File Structure

| File | Responsibility |
|---|---|
| `scripts/fetch-assistant-model.ps1` | Download and SHA-256-check the model into `models/assistant` (git-ignored) |
| `src/Application/Assistant/Quick/ITextEmbedder.cs` | Port: text → unit vector |
| `src/Application/Assistant/Quick/VectorMath.cs` | Dot product, normalisation |
| `src/Application/Assistant/Quick/QuickModels.cs` | `QuickReply`, `QuickAnswer`, `QuickSuggestions`, facts, table, link |
| `src/Application/Assistant/Quick/QuickIntents.cs` | The intents: id, scope, parameter, canonical question |
| `src/Application/Assistant/Quick/QuickCatalogFiles.cs` | Read the embedded phrases and help articles |
| `src/Application/Assistant/Quick/Catalog/en/*` | `phrases.json`, `help/*.md` (embedded resources) |
| `src/Application/Assistant/Quick/PhraseIndex.cs` | In-memory vectors, best match per target, scope filter |
| `src/Application/Assistant/Quick/SpellingCorrector.cs` | Edit-distance correction against the catalog vocabulary |
| `src/Application/Assistant/Quick/QuickIndex.cs` | `QuickIndexState` (published index) and `QuickIndexBuilder` |
| `src/Application/Assistant/Quick/IntentRouter.cs` | Threshold, margin, suggestions; `QuickRouting` settings |
| `src/Application/Assistant/Quick/ParameterResolver.cs` | Order refs, time windows, population areas |
| `src/Application/Assistant/Quick/Fuzzy.cs` | Trigram word similarity for area names |
| `src/Application/Assistant/Quick/QuickText.cs` | Invariant formatting |
| `src/Application/Assistant/Quick/QuickAnswerComposer.cs` | One method per intent, from query results to a reply |
| `src/Application/Assistant/Quick/QuickReplay.cs` | The fixed text Claude is given for a quick reply |
| `src/Application/Assistant/Quick/AskQuick.cs` | Command, validator, handler |
| `src/Application/Assistant/AssistantConversationOpener.cs` | Open or start a conversation; shared by both handlers |
| `src/Application/Products/FindProducts.cs` | Fuzzy product lookup query |
| `src/Infrastructure/Assistant/Quick/*` | `OnnxTextEmbedder`, `QuickModelOptions`, `QuickCatalogLoader`, `QuickRegistration` |
| `src/Api/Assistant/*` | Quick endpoint, DTOs, card mapping, availability, rate policy |
| `frontend/src/features/assistant/*` | Card, chips, Ask the AI, link allow-list |
| `tests/Infrastructure.Tests/Assistant/Quick/evaluation.en.json` | The labelled evaluation set |

---

### Task 1: Fetch the model, and ship it beside the API

**Files:**
- Create: `scripts/fetch-assistant-model.ps1`
- Modify: `.gitignore`, `src/Api/AiFramework.Api.csproj`, `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj`, `scripts/install-prereqs.ps1`

**Interfaces:**
- Produces: `models/assistant/model.onnx` and `models/assistant/vocab.txt` (git-ignored); after a build, `<output>/assistant-model/model.onnx` and `vocab.txt` beside the API's and Infrastructure.Tests' binaries.

- [ ] **Step 1: Write the script**

Create `scripts/fetch-assistant-model.ps1`:

```powershell
<#
.SYNOPSIS
    Downloads the assistant's sentence-embedding model (ADR 0036) into models/assistant.

.DESCRIPTION
    bge-small-en-v1.5, quantised to ONNX (MIT licence), pinned to one Hugging Face commit and
    checked by SHA-256, so every machine, CI run and image embeds with byte-identical weights.
    Idempotent: a file already present with the right hash is kept. The Api project copies the
    folder beside its binaries as assistant-model/ when it exists.
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'models/assistant')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$commit = 'ea104dacec62c0de699686887e3f920caeb4f3e3'
$base = "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/$commit"
$files = @(
    @{ Name = 'model.onnx'; Source = "$base/onnx/model_quantized.onnx"; Sha256 = '6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4' },
    @{ Name = 'vocab.txt'; Source = "$base/vocab.txt"; Sha256 = '07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3' }
)

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

foreach ($file in $files) {
    $path = Join-Path $Destination $file.Name
    if ((Test-Path $path) -and ((Get-FileHash -Algorithm SHA256 $path).Hash -eq $file.Sha256)) {
        Write-Host "ok        $($file.Name)"
        continue
    }

    Write-Host "download  $($file.Name)"
    $partial = "$path.partial"
    Invoke-WebRequest -Uri $file.Source -OutFile $partial -UseBasicParsing
    $actual = (Get-FileHash -Algorithm SHA256 $partial).Hash
    if ($actual -ne $file.Sha256) {
        Remove-Item $partial -Force
        throw "SHA-256 mismatch for $($file.Name): expected $($file.Sha256), got $actual"
    }

    Move-Item -Force $partial $path
}
```

- [ ] **Step 2: Ignore the folder**

Append to `.gitignore`:

```
# The assistant's embedding model (scripts/fetch-assistant-model.ps1), 34 MB, never committed.
models/
```

- [ ] **Step 3: Copy the model beside the API and the Infrastructure tests**

Add to `src/Api/AiFramework.Api.csproj`, inside `<Project>`:

```xml
  <!-- The assistant's embedding model (ADR 0036), fetched by scripts/fetch-assistant-model.ps1 into
       the git-ignored models/assistant, copied beside the binaries as assistant-model/ (the default
       Assistant:Quick:ModelPath). Conditional, so a build without it still succeeds: instant answers
       then report themselves unavailable, and the tests that need the model fail and say why. -->
  <ItemGroup Condition="Exists('$(MSBuildThisFileDirectory)..\..\models\assistant\model.onnx')">
    <None Include="$(MSBuildThisFileDirectory)..\..\models\assistant\*" LinkBase="assistant-model" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

Add the same `ItemGroup` (same paths: the test project is also two levels below the root) to `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj`, with the comment's first line changed to `<!-- The assistant's embedding model, for the embedder tests and the evaluation gate (ADR 0036).`.

- [ ] **Step 4: Check it in install-prereqs**

In `scripts/install-prereqs.ps1`, after the `# --- Playwright browser ---` section and before the npm-config check, add a section in the same style as its neighbours (read them first and match their `Add-Result` calls exactly):

```powershell
# --- Assistant model ----------------------------------------------------------------------------
Write-Host '==> Checking assistant model' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'fetch-assistant-model.ps1')
Add-Result 'Assistant model' 'OK' 'models/assistant (bge-small-en-v1.5)'
```

- [ ] **Step 5: Run it and check the outputs**

Run: `pwsh -NoProfile -File scripts/fetch-assistant-model.ps1`
Expected: `download  model.onnx`, `download  vocab.txt`. Run it again: `ok        model.onnx`, `ok        vocab.txt`.

Run: `dotnet build src/Api --nologo -v q` then `ls src/Api/bin/Debug/net10.0/assistant-model`
Expected: `model.onnx  vocab.txt`.

Run: `git status --short`
Expected: no `models/` entry.

- [ ] **Step 6: Commit**

```bash
git add scripts/fetch-assistant-model.ps1 scripts/install-prereqs.ps1 .gitignore src/Api/AiFramework.Api.csproj tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj
git commit -m "feat(assistant): fetch the embedding model and ship it beside the API

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: The embedder

**Files:**
- Create: `src/Application/Assistant/Quick/ITextEmbedder.cs`, `src/Application/Assistant/Quick/VectorMath.cs`, `src/Infrastructure/Assistant/Quick/QuickModelOptions.cs`, `src/Infrastructure/Assistant/Quick/OnnxTextEmbedder.cs`
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj` (packages), `src/Infrastructure/Assistant/AssistantRegistration.cs`
- Test: `tests/Infrastructure.Tests/Assistant/Quick/OnnxTextEmbedderTests.cs`, `tests/Application.Tests/Assistant/Quick/VectorMathTests.cs`

**Interfaces:**
- Produces: `ITextEmbedder.Embed(string text) : Result<float[]>` (unit length, 384 numbers; `ErrorKind.Unavailable`, code `assistant.quick_unavailable`, when the model is missing or fails); `VectorMath.Dot(ReadOnlySpan<float>, ReadOnlySpan<float>) : double`, `VectorMath.Normalize(float[]) : float[]`; `QuickModelOptions { string ModelPath }` (section `Assistant:Quick`); `OnnxTextEmbedder` registered as the singleton `ITextEmbedder` by `AddAssistant()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Application.Tests/Assistant/Quick/VectorMathTests.cs`:

```csharp
using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class VectorMathTests
{
    [Fact]
    public void Normalize_ScalesToUnitLength()
    {
        var v = VectorMath.Normalize([3f, 4f]);

        v.Should().Equal(0.6f, 0.8f);
    }

    [Fact]
    public void Normalize_LeavesAZeroVectorAlone()
    {
        VectorMath.Normalize([0f, 0f]).Should().Equal(0f, 0f);
    }

    [Fact]
    public void Dot_OfUnitVectors_IsTheirCosine()
    {
        VectorMath.Dot([1f, 0f], [0f, 1f]).Should().Be(0);
        VectorMath.Dot([0.6f, 0.8f], [0.6f, 0.8f]).Should().BeApproximately(1, 1e-6);
    }

    [Fact]
    public void Dot_RefusesVectorsOfDifferentLength()
    {
        var act = () => VectorMath.Dot([1f], [1f, 2f]);

        act.Should().Throw<ArgumentException>();
    }
}
```

Create `tests/Infrastructure.Tests/Assistant/Quick/OnnxTextEmbedderTests.cs`:

```csharp
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
```

(If `Error` names its code property differently from `Code`, use that property; read `src/Application/Abstractions/Result.cs` first.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Application.Tests --filter VectorMathTests --nologo` and `dotnet test tests/Infrastructure.Tests --filter OnnxTextEmbedderTests --nologo`
Expected: both fail to compile: `VectorMath`, `OnnxTextEmbedder`, `QuickModelOptions` not found.

- [ ] **Step 3: Write the port and the maths**

Create `src/Application/Assistant/Quick/ITextEmbedder.cs`:

```csharp
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
```

Create `src/Application/Assistant/Quick/VectorMath.cs`:

```csharp
namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// The only vector maths the router needs. Plain loops: a few hundred 384-number vectors take
/// microseconds, and Application references no numerics package.
/// </summary>
public static class VectorMath
{
    public static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vectors differ in length: {a.Length} and {b.Length}.", nameof(b));
        }

        var sum = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>Scales <paramref name="vector"/> to unit length in place and returns it. A zero vector is left as it is.</summary>
    public static float[] Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var length = Math.Sqrt(Dot(vector, vector));
        if (length == 0)
        {
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / length);
        }

        return vector;
    }
}
```

- [ ] **Step 4: Add the packages**

In `src/Infrastructure/AiFramework.Infrastructure.csproj`, next to the other `PackageReference`s (alphabetical, as the file is):

```xml
    <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.30.0" />
    <PackageReference Include="Microsoft.ML.Tokenizers" Version="2.0.0" />
```

(1.31.0 of ONNX Runtime was three days old when this plan was written; 1.30.0 is from 10 September 2026.)

- [ ] **Step 5: Write the adapter**

Create `src/Infrastructure/Assistant/Quick/QuickModelOptions.cs`:

```csharp
namespace AiFramework.Infrastructure.Assistant.Quick;

/// <summary>
/// Where the embedding model is (Assistant__Quick__ModelPath). Bound by the API's Program.cs; the
/// default is the assistant-model folder the Api project copies beside its binaries. ADR 0036.
/// </summary>
public sealed class QuickModelOptions
{
    public const string SectionName = "Assistant:Quick";

    public string ModelPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "assistant-model");
}
```

Create `src/Infrastructure/Assistant/Quick/OnnxTextEmbedder.cs`:

```csharp
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
            _model.Value?.Session.Dispose();
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

        try
        {
            var tokenizer = BertTokenizer.Create(vocabFile, new BertOptions { LowerCaseBeforeTokenization = true });
            return new Model(new InferenceSession(modelFile), tokenizer);
        }
        catch (OnnxRuntimeException)
        {
            return null;
        }
    }

    private sealed record Model(InferenceSession Session, BertTokenizer Tokenizer);
}
```

- [ ] **Step 6: Register it**

In `src/Infrastructure/Assistant/AssistantRegistration.cs`, add `using AiFramework.Application.Assistant.Quick;` and `using AiFramework.Infrastructure.Assistant.Quick;`, and inside `AddAssistant`, before `return services;`:

```csharp
        // The embedding model for instant answers (ADR 0036). A singleton that loads on first use:
        // the worker registers it too (this method is shared) but never embeds, so never loads it.
        // Options are bound by the API; an unbound host gets the default path.
        services.AddOptions<QuickModelOptions>();
        services.AddSingleton<ITextEmbedder, OnnxTextEmbedder>();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Application.Tests --filter VectorMathTests --nologo` and `dotnet test tests/Infrastructure.Tests --filter OnnxTextEmbedderTests --nologo`
Expected: 4 and 6 passed.

- [ ] **Step 8: Build everything and commit**

Run: `dotnet build --nologo -v q` — expected `0 Warning(s) 0 Error(s)`. Fix any analyzer diagnostic in the new files (warnings are errors) rather than suppressing it.

```bash
git add src/Application/Assistant/Quick src/Infrastructure/Assistant src/Infrastructure/AiFramework.Infrastructure.csproj tests/Application.Tests/Assistant/Quick tests/Infrastructure.Tests/Assistant/Quick
git commit -m "feat(assistant): embed text locally with an ONNX sentence model

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The intents and the catalog files

**Files:**
- Create: `src/Application/Assistant/Quick/QuickIntents.cs`, `src/Application/Assistant/Quick/QuickModels.cs`, `src/Application/Assistant/Quick/QuickCatalogFiles.cs`, `src/Application/Assistant/Quick/Catalog/en/phrases.json`, `src/Application/Assistant/Quick/Catalog/en/help/{place-order,export-orders,change-password,sign-out-everywhere,notifications,catalogue}.md`
- Modify: `src/Application/AiFramework.Application.csproj` (embedded resources)
- Test: `tests/Application.Tests/Assistant/Quick/QuickCatalogTests.cs`

**Interfaces:**
- Consumes: `ToolScope` (existing).
- Produces:
  - `enum QuickParameter { None, Order, Product, Area, Window }`
  - `record QuickIntent(string Id, ToolScope Scope, QuickParameter Parameter, string CanonicalQuestion)`
  - `static class QuickIntents` with constants `OrderStatus = "order_status"`, `RecentOrders = "recent_orders"`, `ExportStatus = "export_status"`, `ProductInfo = "product_info"`, `Population = "population"`, `JobHealth = "job_health"`, `FailedJobs = "failed_jobs"`, `DeadLetters = "dead_letters"`, `Traffic = "traffic"`, `Integrations = "integrations"`, `SignIns = "sign_ins"`, plus `All`, `Find(id)`, `Allows(scope, intent)`
  - `abstract record QuickReply`; `enum QuickAnswerKind { Instant, Help }`; `record QuickFact(string Label, string Value)`; `record QuickTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)`; `record QuickLink(string Label, string To)`; `record QuickAnswer(QuickAnswerKind Kind, string? Title, string Text, IReadOnlyList<QuickFact> Facts, QuickTable? Table, QuickLink? Link, string? AnsweredAs) : QuickReply`; `record QuickSuggestions(string Text, IReadOnlyList<string> Options) : QuickReply`
  - `record QuickPhrase(string IntentId, string Text)`; `record HelpSection(string ArticleId, string Title, string Question, string Heading, string Text, QuickLink Link)`; `record QuickCatalog(IReadOnlyList<QuickPhrase> Phrases, IReadOnlyList<HelpSection> Help)`; `QuickCatalogFiles.Load() : QuickCatalog`; `QuickCatalogFiles.ParseHelp(string markdown) : IReadOnlyList<HelpSection>`

- [ ] **Step 1: Write the failing tests**

Create `tests/Application.Tests/Assistant/Quick/QuickCatalogTests.cs`:

```csharp
using System.Text.RegularExpressions;
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class QuickCatalogTests
{
    private static readonly QuickCatalog Catalog = QuickCatalogFiles.Load();

    /// <summary>The screens a link may open. Kept in step with frontend/src/features/assistant/quickLinks.ts.</summary>
    private static readonly Regex[] Routes =
    [
        new("^/orders$"), new("^/orders/new$"), new("^/orders/exports$"), new("^/orders/[0-9a-f-]{36}$"),
        new("^/products$"), new("^/products/[0-9a-f-]{36}$"), new("^/statistics/population$"),
        new("^/monitoring(/(jobs|traffic|integrations|logins|users))?$"), new("^/account/password$"), new("^/notifications$"),
    ];

    [Fact]
    public void EveryIntent_HasAtLeastTenPhrases_IncludingItsCanonicalQuestion()
    {
        foreach (var intent in QuickIntents.All)
        {
            var phrases = Catalog.Phrases.Where(p => p.IntentId == intent.Id).Select(p => p.Text).ToList();

            phrases.Should().HaveCountGreaterThanOrEqualTo(10, intent.Id);
            phrases.Should().Contain(intent.CanonicalQuestion, "a suggestion chip must always be answered instantly");
        }
    }

    [Fact]
    public void EveryPhrase_BelongsToAKnownIntent()
    {
        Catalog.Phrases.Select(p => p.IntentId).Distinct()
            .Should().BeSubsetOf(QuickIntents.All.Select(i => i.Id));
    }

    [Fact]
    public void NoPhrase_AppearsUnderTwoIntents()
    {
        Catalog.Phrases
            .GroupBy(p => p.Text.Trim().ToUpperInvariant())
            .Where(g => g.Select(p => p.IntentId).Distinct().Count() > 1)
            .Select(g => g.Key)
            .Should().BeEmpty();
    }

    [Fact]
    public void IntentIds_AreUnique()
    {
        QuickIntents.All.Select(i => i.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryHelpArticle_HasALinkToARealScreen_AndAtLeastOneSection()
    {
        Catalog.Help.Should().NotBeEmpty();
        foreach (var article in Catalog.Help.GroupBy(s => s.ArticleId))
        {
            article.Should().NotBeEmpty();
            Routes.Should().Contain(r => r.IsMatch(article.First().Link.To), article.Key);
            article.Should().OnlyContain(s => s.Text.Length > 20 && s.Question.EndsWith('?'));
        }
    }

    [Fact]
    public void ParseHelp_ReadsTheFrontMatterAndOneSectionPerHeading()
    {
        const string markdown = """
            ---
            id: sample
            title: A sample
            question: How do I sample?
            link: /orders
            linkLabel: All orders
            ---
            ## First
            The first section's text, which is long enough.

            ## Second
            The second section's text,
            over two lines.
            """;

        var sections = QuickCatalogFiles.ParseHelp(markdown);

        sections.Should().HaveCount(2);
        sections[0].Should().Be(new HelpSection(
            "sample", "A sample", "How do I sample?", "First", "The first section's text, which is long enough.", new QuickLink("All orders", "/orders")));
        sections[1].Text.Should().Be("The second section's text, over two lines.");
    }

    [Fact]
    public void Allows_GivesAMemberOnlyMemberIntents_AndAnOperatorAll()
    {
        var jobHealth = QuickIntents.Find(QuickIntents.JobHealth)!;
        var orders = QuickIntents.Find(QuickIntents.RecentOrders)!;

        QuickIntents.Allows(ToolScope.Member, jobHealth).Should().BeFalse();
        QuickIntents.Allows(ToolScope.Member, orders).Should().BeTrue();
        QuickIntents.Allows(ToolScope.Operator, jobHealth).Should().BeTrue();
    }
}
```

(`!` is allowed in C# tests here only if the repo's analyzers allow the null-forgiving operator in tests; commit `07ca9ee` removed one Sonar flagged (S8969). If the build flags it, use `QuickIntents.Find(...) ?? throw new InvalidOperationException()` instead.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Application.Tests --filter QuickCatalogTests --nologo`
Expected: compile failure, `QuickCatalogFiles` not found.

- [ ] **Step 3: Write the intents and the reply models**

Create `src/Application/Assistant/Quick/QuickIntents.cs`:

```csharp
namespace AiFramework.Application.Assistant.Quick;

/// <summary>What an intent must resolve from the question before it can answer.</summary>
public enum QuickParameter
{
    None,
    Order,
    Product,
    Area,
    Window,
}

/// <summary>
/// One question the router answers without a model: who may ask it, what it must resolve, and the
/// question a suggestion chip sends for it. The canonical question is one of the intent's own
/// example phrases, so a chip is always answered instantly. ADR 0036.
/// </summary>
public sealed record QuickIntent(string Id, ToolScope Scope, QuickParameter Parameter, string CanonicalQuestion);

public static class QuickIntents
{
    public const string OrderStatus = "order_status";
    public const string RecentOrders = "recent_orders";
    public const string ExportStatus = "export_status";
    public const string ProductInfo = "product_info";
    public const string Population = "population";
    public const string JobHealth = "job_health";
    public const string FailedJobs = "failed_jobs";
    public const string DeadLetters = "dead_letters";
    public const string Traffic = "traffic";
    public const string Integrations = "integrations";
    public const string SignIns = "sign_ins";

    public static IReadOnlyList<QuickIntent> All { get; } =
    [
        new(OrderStatus, ToolScope.Member, QuickParameter.Order, "Has my last order shipped?"),
        new(RecentOrders, ToolScope.Member, QuickParameter.None, "Show my recent orders"),
        new(ExportStatus, ToolScope.Member, QuickParameter.None, "Is my export ready?"),
        new(ProductInfo, ToolScope.Member, QuickParameter.Product, "What does a product cost?"),
        new(Population, ToolScope.Member, QuickParameter.Area, "What is the population of Denmark?"),
        new(JobHealth, ToolScope.Operator, QuickParameter.Window, "Did any background job fail today?"),
        new(FailedJobs, ToolScope.Operator, QuickParameter.None, "Which jobs failed?"),
        new(DeadLetters, ToolScope.Operator, QuickParameter.None, "Is anything dead-lettered?"),
        new(Traffic, ToolScope.Operator, QuickParameter.Window, "How is the API doing?"),
        new(Integrations, ToolScope.Operator, QuickParameter.None, "Are the partner systems up?"),
        new(SignIns, ToolScope.Operator, QuickParameter.Window, "Are any accounts locked out?"),
    ];

    public static QuickIntent? Find(string id) =>
        All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    /// <summary>A member gets member intents; an operator gets every intent.</summary>
    public static bool Allows(ToolScope scope, QuickIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return scope == ToolScope.Operator || intent.Scope == ToolScope.Member;
    }
}
```

Create `src/Application/Assistant/Quick/QuickModels.cs`:

```csharp
namespace AiFramework.Application.Assistant.Quick;

/// <summary>What the quick path says: an answer, or suggestions when it is not sure. ADR 0036.</summary>
#pragma warning disable S2094 // A closed hierarchy's base, as AssistantTurn: value equality on both reply types
public abstract record QuickReply;
#pragma warning restore S2094

public enum QuickAnswerKind
{
    /// <summary>Figures from a query, for the caller.</summary>
    Instant,

    /// <summary>A help article's section, quoted.</summary>
    Help,
}

public sealed record QuickFact(string Label, string Value);

public sealed record QuickTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>An in-app path, such as /orders/{id}. The page renders it only if it matches its allow-list.</summary>
public sealed record QuickLink(string Label, string To);

/// <summary>
/// One answer, already formatted: every figure in it came from a query. <see cref="Title"/> is the
/// help article's title; <see cref="AnsweredAs"/> is the corrected question when the spelling pass
/// made the match.
/// </summary>
public sealed record QuickAnswer(
    QuickAnswerKind Kind,
    string? Title,
    string Text,
    IReadOnlyList<QuickFact> Facts,
    QuickTable? Table,
    QuickLink? Link,
    string? AnsweredAs) : QuickReply;

/// <summary>Not sure: the closest canonical questions, each answered instantly when chosen.</summary>
public sealed record QuickSuggestions(string Text, IReadOnlyList<string> Options) : QuickReply;
```

- [ ] **Step 4: Write the loader**

Create `src/Application/Assistant/Quick/QuickCatalogFiles.cs`:

```csharp
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AiFramework.Application.Assistant.Quick;

public sealed record QuickPhrase(string IntentId, string Text);

/// <summary>One section of a help article: what the answer quotes, and the article it belongs to.</summary>
public sealed record HelpSection(string ArticleId, string Title, string Question, string Heading, string Text, QuickLink Link);

public sealed record QuickCatalog(IReadOnlyList<QuickPhrase> Phrases, IReadOnlyList<HelpSection> Help);

/// <summary>
/// Reads the catalog embedded in this assembly: Catalog/en/phrases.json and Catalog/en/help/*.md.
/// Data, not code, so a phrase or an article changes in review like any other file, and the
/// evaluation gate checks the result. ADR 0036.
/// </summary>
public static class QuickCatalogFiles
{
    private const string Prefix = "quick/en/";

    public static QuickCatalog Load()
    {
        var assembly = typeof(QuickCatalogFiles).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Select(n => (Resource: n, Path: n.Replace('\\', '/')))
            .Where(n => n.Path.StartsWith(Prefix, StringComparison.Ordinal))
            .ToList();

        var phrasesFile = names.Single(n => n.Path == Prefix + "phrases.json");
        var phrases = ParsePhrases(Read(assembly, phrasesFile.Resource));

        var help = names
            .Where(n => n.Path.StartsWith(Prefix + "help/", StringComparison.Ordinal) && n.Path.EndsWith(".md", StringComparison.Ordinal))
            .OrderBy(n => n.Path, StringComparer.Ordinal)
            .SelectMany(n => ParseHelp(Read(assembly, n.Resource)))
            .ToList();

        return new QuickCatalog(phrases, help);
    }

    public static IReadOnlyList<HelpSection> ParseHelp(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            throw new FormatException("A help article starts with a --- front-matter block.");
        }

        var front = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 1;
        for (; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                front[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        string Field(string name) => front.TryGetValue(name, out var value) && value.Length > 0
            ? value
            : throw new FormatException($"A help article's front matter needs '{name}'.");

        var link = new QuickLink(Field("linkLabel"), Field("link"));
        var sections = new List<HelpSection>();
        string? heading = null;
        var text = new StringBuilder();

        void Close()
        {
            if (heading is not null)
            {
                sections.Add(new HelpSection(Field("id"), Field("title"), Field("question"), heading, text.ToString().Trim(), link));
            }

            text.Clear();
        }

        for (i++; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                heading = line[3..].Trim();
            }
            else if (line.Length > 0)
            {
                text.Append(text.Length > 0 ? " " : string.Empty).Append(line);
            }
        }

        Close();
        return sections;
    }

    private static List<QuickPhrase> ParsePhrases(string json)
    {
        using var document = JsonDocument.Parse(json);
        return
        [
            .. document.RootElement.GetProperty("intents").EnumerateObject()
                .SelectMany(intent => intent.Value.EnumerateArray()
                    .Select(phrase => new QuickPhrase(intent.Name, phrase.GetString() ?? string.Empty))),
        ];
    }

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource '{resource}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 5: Embed the files**

In `src/Application/AiFramework.Application.csproj`, add:

```xml
  <!-- The instant-answer catalog (ADR 0036). LogicalName gives a stable "quick/en/..." name; the
       loader normalises the separator, which is "\" on Windows. -->
  <ItemGroup>
    <EmbeddedResource Include="Assistant\Quick\Catalog\**\*.json;Assistant\Quick\Catalog\**\*.md"
                      LogicalName="quick/%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>
```

- [ ] **Step 6: Write the phrases**

Create `src/Application/Assistant/Quick/Catalog/en/phrases.json`:

```json
{
  "intents": {
    "order_status": [
      "Has my last order shipped?",
      "Has my order shipped yet?",
      "Where is my latest order?",
      "What is the status of my last order?",
      "Is my order on its way?",
      "Did my most recent order ship?",
      "Track my last order",
      "What happened to my order?",
      "Was my order cancelled?",
      "Is my latest order still pending?",
      "Check the status of my order",
      "Where's my order?",
      "Has order shipped?",
      "Status of order"
    ],
    "recent_orders": [
      "Show my recent orders",
      "List my orders",
      "What have I ordered?",
      "Show me my last few orders",
      "My orders",
      "Which orders have I placed?",
      "Show my order history",
      "What did I order recently?",
      "Give me an overview of my orders",
      "Show all my orders"
    ],
    "export_status": [
      "Is my export ready?",
      "Has my order export finished?",
      "Can I download my export yet?",
      "Where is my PDF export?",
      "Is my orders PDF ready?",
      "Status of my export",
      "Did my export finish?",
      "When will my export be ready?",
      "Show my exports",
      "Is the export of my orders done?"
    ],
    "product_info": [
      "What does a product cost?",
      "How much is the Widget?",
      "What is the price of the Gadget?",
      "How much does the Sprocket cost?",
      "Price of Widget Pro",
      "What does Widget cost?",
      "Is the Gadget expensive?",
      "Tell me about the Widget",
      "Find the product Sprocket",
      "Look up product SKU-1042",
      "What is the price of SKU-1042?"
    ],
    "population": [
      "What is the population of Denmark?",
      "What is the population of Copenhagen?",
      "How many people live in Aarhus?",
      "Population of Odense",
      "How many inhabitants does Copenhagen have?",
      "How many people live in Denmark?",
      "Show the population of Region Hovedstaden",
      "What's the latest population figure for Aalborg?",
      "Population figures for Frederiksberg",
      "How big is the population of Esbjerg?"
    ],
    "job_health": [
      "Did any background job fail today?",
      "Any failed jobs today?",
      "How are the background jobs doing?",
      "Are the jobs healthy?",
      "Job health in the last 24 hours",
      "Did anything fail in the last hour?",
      "How many jobs failed this week?",
      "Are background jobs running fine?",
      "Job status overview",
      "Any job failures recently?"
    ],
    "failed_jobs": [
      "Which jobs failed?",
      "Show the failed job runs",
      "List failed jobs",
      "What were the last job failures?",
      "Show me job runs that failed",
      "Which background jobs have errors?",
      "Recent failed runs",
      "What failed in the job runner?",
      "Show failing jobs",
      "Give me the list of failed job runs"
    ],
    "dead_letters": [
      "Is anything dead-lettered?",
      "Show the dead letters",
      "Are there any dead-lettered messages?",
      "List the dead letter queue",
      "Which messages ended up in dead letters?",
      "Any messages that exhausted their retries?",
      "Show messages that could not be delivered",
      "How many dead letters are there?",
      "Dead letter queue status",
      "Are messages stuck in the dead letter queue?"
    ],
    "traffic": [
      "How is the API doing?",
      "What is the error rate?",
      "How much traffic are we getting?",
      "Show API traffic for the last hour",
      "What is the latency right now?",
      "Is the API slow?",
      "Requests per minute",
      "How many requests failed in the last hour?",
      "API health and response times",
      "What does the traffic look like today?"
    ],
    "integrations": [
      "Are the partner systems up?",
      "Are external systems healthy?",
      "Is Statistics Denmark reachable?",
      "Show the status of the integrations",
      "Are any integrations down?",
      "Which external systems are unhealthy?",
      "Check partner connections",
      "Integration health",
      "Are our third-party systems working?",
      "Is any external system failing?"
    ],
    "sign_ins": [
      "Are any accounts locked out?",
      "Any lockouts?",
      "How many sign-ins failed today?",
      "Show sign-in health",
      "Are people having trouble signing in?",
      "How many users signed in today?",
      "Are there failed logins?",
      "Sign-in statistics for the last 24 hours",
      "How many users are online now?",
      "Is someone trying to guess passwords?"
    ]
  }
}
```

- [ ] **Step 7: Write the help articles**

Plain text only: the page renders sections as text, never Markdown. Button names are the app's real labels.

`src/Application/Assistant/Quick/Catalog/en/help/place-order.md`:

```markdown
---
id: place-order
title: Placing an order
question: How do I place an order?
link: /orders/new
linkLabel: Place an order
---
## Place an order
Open Place an order, choose a product from the catalogue and enter the quantity, then choose Place order. The order appears at the top of All orders straight away.

## After you order
You get a notification when the order is placed and another when it ships. Open the order from All orders to see its status, price and total.
```

`src/Application/Assistant/Quick/Catalog/en/help/export-orders.md`:

```markdown
---
id: export-orders
title: Exporting your orders
question: How do I export my orders as a PDF?
link: /orders/exports
linkLabel: Go to Exports
---
## Request an export
Open Exports and choose Export my orders. The PDF of your orders is built in the background, so you can keep working while it is prepared.

## When it is ready
You get a notification when the export is ready. On Exports, choose View to read it in the app or Download to save the PDF.
```

`src/Application/Assistant/Quick/Catalog/en/help/change-password.md`:

```markdown
---
id: change-password
title: Changing your password
question: How do I change my password?
link: /account/password
linkLabel: Change password
---
## Change your password
Open Password under Account, enter your current password and your new one, then choose Change password.

## What happens next
Your other sessions are signed out when the password changes, so any device still signed in with the old password has to sign in again.
```

`src/Application/Assistant/Quick/Catalog/en/help/sign-out-everywhere.md`:

```markdown
---
id: sign-out-everywhere
title: Signing out everywhere
question: How do I sign out of all my devices?
link: /account/password
linkLabel: Open account settings
---
## Sign out of every device
Open Password under Account and, under Other sessions, choose Sign out everywhere. Every session signed in with your account ends, including on other devices.
```

`src/Application/Assistant/Quick/Catalog/en/help/notifications.md`:

```markdown
---
id: notifications
title: Notifications
question: How do notifications work?
link: /notifications
linkLabel: Open notifications
---
## Your notifications
The bell at the top of every page shows how many notifications you have not read. Open it to see them all, newest first, and use View to go to what a notification is about.

## Marking them read
Choose Mark read on one notification, or Mark all read to clear them all at once.
```

`src/Application/Assistant/Quick/Catalog/en/help/catalogue.md`:

```markdown
---
id: catalogue
title: The product catalogue
question: Where can I see the products?
link: /products
linkLabel: Open the catalogue
---
## Browse the catalogue
Products under Catalogue lists every product with its SKU and price. Open a product to see its description.

## Adding or changing products
Only administrators can add or edit products. If something is missing or wrong, ask an administrator.
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/Application.Tests --filter QuickCatalogTests --nologo`
Expected: 7 passed.

- [ ] **Step 9: Commit**

```bash
git add src/Application/Assistant/Quick src/Application/AiFramework.Application.csproj tests/Application.Tests/Assistant/Quick
git commit -m "feat(assistant): describe the instant-answer intents and help articles

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The phrase index and the router

**Files:**
- Create: `src/Application/Assistant/Quick/PhraseIndex.cs`, `src/Application/Assistant/Quick/QuickIndex.cs`, `src/Application/Assistant/Quick/IntentRouter.cs`, `src/Application/Assistant/Quick/SpellingCorrector.cs` (a minimal pass-through version here; Task 5 gives it its behaviour)
- Test: `tests/Application.Tests/Assistant/Quick/IntentRouterTests.cs`, `tests/Application.Tests/Assistant/Quick/FakeEmbedder.cs`

**Interfaces:**
- Consumes: `ITextEmbedder`, `VectorMath` (Task 2); `QuickCatalog`, `HelpSection`, `QuickIntent`, `QuickIntents` (Task 3).
- Produces:
  - `enum QuickTargetKind { Intent, Help }`; `record QuickTarget(QuickTargetKind Kind, string Id)`; `record QuickMatch(QuickTarget Target, double Score, HelpSection? Section)`
  - `PhraseIndex.Build(QuickCatalog, ITextEmbedder) : Result<PhraseIndex>`; `PhraseIndex.Search(float[] query, ToolScope scope) : IReadOnlyList<QuickMatch>` (best match per target, best first); `PhraseIndex.Count : int`
  - `SpellingCorrector(IEnumerable<string> texts)`; `SpellingCorrector.Correct(string question) : string?` (null when nothing changed)
  - `QuickIndexState` (singleton): `Publish(PhraseIndex, SpellingCorrector)`, `TryGet(out PhraseIndex?, out SpellingCorrector?) : bool`, `IsReady`
  - `QuickIndexBuilder.Build(QuickCatalog, ITextEmbedder) : Result<(PhraseIndex Index, SpellingCorrector Spelling)>`
  - `record QuickRouting(double Threshold, double Margin, double SuggestionFloor)` with `Default`
  - `abstract record RouteDecision`; `record RouteToIntent(QuickIntent Intent, double Score, string? AnsweredAs)`; `record RouteToHelp(HelpSection Section, double Score, string? AnsweredAs)`; `record RouteUnsure(IReadOnlyList<string> Suggestions)`
  - `IntentRouter(QuickIndexState, ITextEmbedder, QuickRouting).Route(string question, ToolScope scope) : Result<RouteDecision>`; `IntentRouter.MaxSuggestions = 3`

- [ ] **Step 1: Write the fake embedder and the failing tests**

Create `tests/Application.Tests/Assistant/Quick/FakeEmbedder.cs`:

```csharp
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

    public Result<float[]> Embed(string text) =>
        Unavailable
            ? Result.Failure<float[]>(new Error(ErrorKind.Unavailable, "assistant.quick_unavailable", "Unavailable."))
            : _vectors.TryGetValue(text, out var vector)
                ? Result.Success(vector)
                : throw new InvalidOperationException($"FakeEmbedder has no vector for '{text}'.");
}
```

Create `tests/Application.Tests/Assistant/Quick/IntentRouterTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class IntentRouterTests
{
    private static readonly QuickRouting Routing = new(Threshold: 0.8, Margin: 0.05, SuggestionFloor: 0.5);

    // Three axes: orders, exports, jobs. A fourth carries the help article.
    private static readonly QuickCatalog Catalog = new(
        [
            new(QuickIntents.RecentOrders, "Show my recent orders"),
            new(QuickIntents.ExportStatus, "Is my export ready?"),
            new(QuickIntents.JobHealth, "Did any background job fail today?"),
        ],
        [new HelpSection("export-orders", "Exporting your orders", "How do I export my orders as a PDF?", "Request an export", "Open Exports and choose Export my orders.", new QuickLink("Go to Exports", "/orders/exports"))]);

    private static FakeEmbedder Embedder() => new FakeEmbedder()
        .Add("Show my recent orders", 1, 0, 0, 0)
        .Add("Is my export ready?", 0, 1, 0, 0)
        .Add("Did any background job fail today?", 0, 0, 1, 0)
        .Add("How do I export my orders as a PDF?", 0, 0, 0, 1)
        .Add("Request an export. Open Exports and choose Export my orders.", 0, 0.2f, 0, 1);

    private static IntentRouter Router(FakeEmbedder embedder)
    {
        var state = new QuickIndexState();
        var built = QuickIndexBuilder.Build(Catalog, embedder).Value;
        state.Publish(built.Index, built.Spelling);
        return new IntentRouter(state, embedder, Routing);
    }

    [Fact]
    public void AClearMatch_IsAnsweredInstantly()
    {
        // Only catalog words: from Task 5 on, the spelling pass would turn "list" into "last", and
        // the fake embedder throws on a text it has no vector for.
        var embedder = Embedder().Add("show my orders please", 0.98f, 0.1f, 0, 0);

        var decision = Router(embedder).Route("show my orders please", ToolScope.Member).Value;

        decision.Should().BeOfType<RouteToIntent>().Which.Intent.Id.Should().Be(QuickIntents.RecentOrders);
    }

    [Fact]
    public void AMatchBelowTheThreshold_IsUnsure_AndSuggestsTheClosest()
    {
        var embedder = Embedder().Add("orders and exports?", 0.6f, 0.55f, 0, 0);

        var decision = Router(embedder).Route("orders and exports?", ToolScope.Member).Value;

        decision.Should().BeOfType<RouteUnsure>().Which.Suggestions
            .Should().Equal("Show my recent orders", "Is my export ready?");
    }

    [Fact]
    public void TwoIntentsTooCloseToCall_AreUnsure_EvenAboveTheThreshold()
    {
        // Orders and exports 0.9 alike here, and the question halfway between: both score 0.975,
        // well above the threshold, so only the margin refuses to pick one. (With orthogonal
        // intents no question could score above 0.8 on two of them, and this would test nothing.)
        var embedder = new FakeEmbedder()
            .Add("Show my recent orders", 1, 0, 0, 0)
            .Add("Is my export ready?", 0.9f, 0.436f, 0, 0)
            .Add("Did any background job fail today?", 0, 0, 1, 0)
            .Add("How do I export my orders as a PDF?", 0, 0, 0, 1)
            .Add("Request an export. Open Exports and choose Export my orders.", 0, 0.2f, 0, 1)
            .Add("my orders export", 1.9f, 0.436f, 0, 0);

        var decision = Router(embedder).Route("my orders export", ToolScope.Member).Value;

        decision.Should().BeOfType<RouteUnsure>().Which.Suggestions
            .Should().Contain("Show my recent orders").And.Contain("Is my export ready?");
    }

    [Fact]
    public void AMember_NeverReachesAnOperatorIntent_NotEvenAsASuggestion()
    {
        var embedder = Embedder().Add("did a job fail", 0, 0, 1, 0);

        var decision = Router(embedder).Route("did a job fail", ToolScope.Member).Value;

        var unsure = decision.Should().BeOfType<RouteUnsure>().Subject;
        unsure.Suggestions.Should().NotContain("Did any background job fail today?");
    }

    [Fact]
    public void AnOperator_ReachesTheOperatorIntent()
    {
        var embedder = Embedder().Add("did a job fail", 0, 0, 1, 0);

        var decision = Router(embedder).Route("did a job fail", ToolScope.Operator).Value;

        decision.Should().BeOfType<RouteToIntent>().Which.Intent.Id.Should().Be(QuickIntents.JobHealth);
    }

    [Fact]
    public void AHowToQuestion_LandsOnTheHelpSection()
    {
        var embedder = Embedder().Add("how can I get a pdf of my orders", 0, 0.1f, 0, 1);

        var decision = Router(embedder).Route("how can I get a pdf of my orders", ToolScope.Member).Value;

        decision.Should().BeOfType<RouteToHelp>().Which.Section.ArticleId.Should().Be("export-orders");
    }

    [Fact]
    public void NothingNearby_IsUnsure_WithNoSuggestions()
    {
        var embedder = Embedder().Add("write a poem", -1, -1, -1, -1);

        var decision = Router(embedder).Route("write a poem", ToolScope.Member).Value;

        decision.Should().BeOfType<RouteUnsure>().Which.Suggestions.Should().BeEmpty();
    }

    [Fact]
    public void BeforeTheIndexIsPublished_RoutingIsUnavailable()
    {
        var router = new IntentRouter(new QuickIndexState(), Embedder(), Routing);

        var result = router.Route("anything", ToolScope.Member);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }

    [Fact]
    public void WhenTheEmbedderFails_RoutingIsUnavailable()
    {
        var embedder = Embedder();
        var router = Router(embedder);
        embedder.Unavailable = true;

        router.Route("anything", ToolScope.Member).Error.Kind.Should().Be(ErrorKind.Unavailable);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Application.Tests --filter IntentRouterTests --nologo`
Expected: compile failure (`IntentRouter`, `QuickIndexState`, `QuickIndexBuilder` not found).

- [ ] **Step 3: Write the index**

Create `src/Application/Assistant/Quick/PhraseIndex.cs`:

```csharp
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
```

- [ ] **Step 4: Write a pass-through spelling corrector (Task 5 fills it in)**

Create `src/Application/Assistant/Quick/SpellingCorrector.cs`:

```csharp
namespace AiFramework.Application.Assistant.Quick;

/// <summary>Corrects misspelt words against the catalog's own vocabulary. Task 5 gives it its behaviour.</summary>
public sealed class SpellingCorrector(IEnumerable<string> texts)
{
    private readonly IReadOnlyList<string> _texts = [.. texts];

    public int VocabularySize => _texts.Count;

    /// <summary>The corrected question, or null when nothing was changed.</summary>
    public string? Correct(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return null;
    }
}
```

- [ ] **Step 5: Write the state, the builder and the router**

Create `src/Application/Assistant/Quick/QuickIndex.cs`:

```csharp
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
```

Create `src/Application/Assistant/Quick/IntentRouter.cs`:

```csharp
using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// How sure the router must be. <see cref="Threshold"/>: the least similarity for an instant
/// answer. <see cref="Margin"/>: the least lead over the next target, so a close call between two
/// intents is a suggestion, never a guess. <see cref="SuggestionFloor"/>: below this a target is not
/// worth suggesting. Bound by the API from Assistant__Quick__*; the defaults come from the
/// evaluation gate (Task 13). ADR 0036.
/// </summary>
public sealed record QuickRouting(double Threshold, double Margin, double SuggestionFloor)
{
    public static QuickRouting Default { get; } = new(Threshold: 0.80, Margin: 0.04, SuggestionFloor: 0.55);
}

#pragma warning disable S2094 // A closed hierarchy's base, as AssistantTurn
public abstract record RouteDecision;
#pragma warning restore S2094

public sealed record RouteToIntent(QuickIntent Intent, double Score, string? AnsweredAs) : RouteDecision;

public sealed record RouteToHelp(HelpSection Section, double Score, string? AnsweredAs) : RouteDecision;

public sealed record RouteUnsure(IReadOnlyList<string> Suggestions) : RouteDecision;

public sealed class IntentRouter(QuickIndexState state, ITextEmbedder embedder, QuickRouting routing)
{
    public const int MaxSuggestions = 3;

    private static readonly Error NotReady = new(
        ErrorKind.Unavailable, "assistant.quick_unavailable", "Instant answers aren't available right now.");

    public Result<RouteDecision> Route(string question, ToolScope scope)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (!state.TryGet(out var index, out var spelling))
        {
            return Result.Failure<RouteDecision>(NotReady);
        }

        var original = embedder.Embed(question);
        if (!original.IsSuccess)
        {
            return Result.Failure<RouteDecision>(original.Error);
        }

        var (decision, score) = Decide(index.Search(original.Value, scope), answeredAs: null);

        // The correction is a second opinion, used only when it makes an instant match the
        // original question did not, or a better one.
        if (spelling.Correct(question) is { } corrected)
        {
            var embedded = embedder.Embed(corrected);
            if (embedded.IsSuccess)
            {
                var (alternative, alternativeScore) = Decide(index.Search(embedded.Value, scope), corrected);
                if (alternative is not RouteUnsure && (decision is RouteUnsure || alternativeScore > score))
                {
                    decision = alternative;
                }
            }
        }

        return Result.Success(decision);
    }

    private (RouteDecision Decision, double Score) Decide(IReadOnlyList<QuickMatch> matches, string? answeredAs)
    {
        if (matches.Count == 0)
        {
            return (new RouteUnsure([]), 0);
        }

        var top = matches[0];
        var runnerUp = matches.Count > 1 ? matches[1].Score : 0;
        if (top.Score >= routing.Threshold && top.Score - runnerUp >= routing.Margin)
        {
            RouteDecision decision = top.Target.Kind == QuickTargetKind.Intent
                ? new RouteToIntent(QuickIntents.Find(top.Target.Id) ?? throw new InvalidOperationException(top.Target.Id), top.Score, answeredAs)
                : new RouteToHelp(top.Section ?? throw new InvalidOperationException(top.Target.Id), top.Score, answeredAs);
            return (decision, top.Score);
        }

        var suggestions = matches
            .Where(m => m.Score >= routing.SuggestionFloor)
            .Take(MaxSuggestions)
            .Select(m => m.Target.Kind == QuickTargetKind.Intent
                ? QuickIntents.Find(m.Target.Id)?.CanonicalQuestion
                : m.Section?.Question)
            .OfType<string>()
            .ToList();

        return (new RouteUnsure(suggestions), top.Score);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Application.Tests --filter "IntentRouterTests|QuickCatalogTests|VectorMathTests" --nologo`
Expected: all passed. If `AHowToQuestion_LandsOnTheHelpSection` fails because the help article's question and section are both embedded, check that the test's fake has a vector for exactly `"{Heading}. {Text}"` as `PhraseIndex.Build` composes it.

- [ ] **Step 7: Commit**

```bash
git add src/Application/Assistant/Quick tests/Application.Tests/Assistant/Quick
git commit -m "feat(assistant): route a question to an intent by threshold and margin

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The spelling corrector

**Files:**
- Modify: `src/Application/Assistant/Quick/SpellingCorrector.cs` (replace the pass-through)
- Test: `tests/Application.Tests/Assistant/Quick/SpellingCorrectorTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `SpellingCorrector(IEnumerable<string> texts)`, `Correct(string question) : string?`, `VocabularySize : int`, `MinLength = 4`, `static EditDistance(string a, string b, int limit) : int`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Application.Tests/Assistant/Quick/SpellingCorrectorTests.cs`:

```csharp
using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class SpellingCorrectorTests
{
    private static readonly SpellingCorrector Corrector = new(
    [
        "Has my last order shipped?", "Is my export ready?", "Show the dead letters", "Status of order", "card", "cart",
    ]);

    [Fact]
    public void Correct_FixesAMisspeltWordAgainstTheVocabulary()
    {
        Corrector.Correct("has my last order shiped").Should().Be("has my last order shipped");
    }

    [Fact]
    public void Correct_FixesASwappedPairOfLetters()
    {
        Corrector.Correct("is my exprot ready").Should().Be("is my export ready");
    }

    [Fact]
    public void Correct_ReturnsNull_WhenEveryWordIsKnown()
    {
        Corrector.Correct("Is my export ready?").Should().BeNull();
    }

    [Fact]
    public void Correct_LeavesShortWordsAlone()
    {
        Corrector.Correct("teh export").Should().BeNull();
    }

    [Fact]
    public void Correct_LeavesAnIdAlone()
    {
        Corrector.Correct("status of order 3f2a1b9c").Should().BeNull();
    }

    [Fact]
    public void Correct_LeavesQuotedTextAlone()
    {
        Corrector.Correct("status of \"shiped\"").Should().BeNull();
    }

    [Fact]
    public void Correct_RefusesATie()
    {
        // "carx" is one edit from both "card" and "cart": no guess.
        Corrector.Correct("carx").Should().BeNull();
    }

    [Fact]
    public void Correct_LeavesAWordWithNoCloseMatch()
    {
        Corrector.Correct("write a poem about rainfall").Should().BeNull();
    }

    [Theory]
    [InlineData("order", "order", 0)]
    [InlineData("shiped", "shipped", 1)]
    [InlineData("odrer", "order", 1)]
    [InlineData("abcdef", "uvwxyz", 3)]
    public void EditDistance_CountsInsertionsDeletionsSubstitutionsAndSwaps(string a, string b, int expected)
    {
        SpellingCorrector.EditDistance(a, b, limit: 2).Should().Be(Math.Min(expected, 3));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Application.Tests --filter SpellingCorrectorTests --nologo`
Expected: compile failure (`EditDistance` not found); once that compiles, the correction tests fail because `Correct` returns null.

- [ ] **Step 3: Write the corrector**

Replace `src/Application/Assistant/Quick/SpellingCorrector.cs`:

```csharp
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
    [GeneratedRegex("\"[^\"]*\"|\\b[A-Za-z]+\\b")]
    private static partial Regex Token();

    [GeneratedRegex("[A-Za-z]+")]
    private static partial Regex Word();
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test tests/Application.Tests --filter "SpellingCorrectorTests|IntentRouterTests" --nologo`
Expected: all passed. `IntentRouterTests` must still pass: the corrector now changes questions, and `FakeEmbedder` throws on a text it has no vector for, so a router test whose question gets corrected fails loudly. If one does, give the fake a vector for the corrected text in that test, not a different question.

- [ ] **Step 5: Commit**

```bash
git add src/Application/Assistant/Quick/SpellingCorrector.cs tests/Application.Tests/Assistant/Quick/SpellingCorrectorTests.cs
git commit -m "feat(assistant): correct misspelt words against the catalog vocabulary

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Parameters, and finding a product by name

**Files:**
- Create: `src/Application/Assistant/Quick/Fuzzy.cs`, `src/Application/Assistant/Quick/ParameterResolver.cs`, `src/Application/Products/FindProducts.cs`, a migration `src/Infrastructure/Persistence/Migrations/*_EnablePgTrgm.cs` (generated)
- Modify: `src/Application/Products/IProductRepository.cs`, `src/Infrastructure/Persistence/ProductRepository.cs`, `src/Infrastructure/Persistence/AiFrameworkDbContext.cs`, `src/Infrastructure/InfrastructureRegistration.cs`
- Test: `tests/Application.Tests/Assistant/Quick/ParameterResolverTests.cs`, `tests/Application.Tests/Assistant/Quick/FuzzyTests.cs`, `tests/Application.Tests/Products/FindProductsHandlerTests.cs`, `tests/Infrastructure.Tests/Persistence/ProductRepositoryFindTests.cs`

**Interfaces:**
- Consumes: `IQueryDispatcher`, `GetPopulationAreas` → `PopulationAreasView(IReadOnlyList<PopulationAreaView> Areas, string Source)`, `PopulationAreaView(string Code, string Name)`, `Product.NormalizeSku`.
- Produces:
  - `Fuzzy.WordSimilarity(string name, string text) : double` (0–1), `Fuzzy.Fold(string) : string`
  - `ParameterResolver(IQueryDispatcher)`; statics `OrderId(string) : Guid?`, `OrderPrefix(string) : string?` (8 lower-case hex), `Window(string, TimeSpan fallback) : TimeSpan`; `AreaAsync(string question, CancellationToken) : Task<Result<AreaResolution>>`; `record AreaResolution(PopulationAreaView? Area, IReadOnlyList<PopulationAreaView> Candidates)`; constants `AreaMatch = 0.5`, `AreaLead = 0.1`
  - `record ProductMatch(Guid Id, string Sku, string Name, decimal Price, double Score)`; `record FindProducts(string Text, int Limit) : IQuery<IReadOnlyList<ProductMatch>>`; `FindProductsHandler.MaxLimit = 10`, `MinimumSimilarity = 0.5`
  - `IProductRepository.FindAsync(string text, IReadOnlyList<string> skus, double minimumSimilarity, int limit, CancellationToken) : Task<IReadOnlyList<ProductMatch>>`

- [ ] **Step 1: Write the failing Application tests**

Create `tests/Application.Tests/Assistant/Quick/FuzzyTests.cs`:

```csharp
using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class FuzzyTests
{
    [Fact]
    public void WordSimilarity_IsOne_ForANameThatAppearsExactly()
    {
        Fuzzy.WordSimilarity("Copenhagen", "What is the population of Copenhagen?").Should().Be(1);
    }

    [Fact]
    public void WordSimilarity_StaysHigh_ForATypo()
    {
        Fuzzy.WordSimilarity("Copenhagen", "population of copenhagn").Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void WordSimilarity_FindsAPartOfALongerName()
    {
        Fuzzy.WordSimilarity("All Denmark", "What is the population of Denmark?").Should().BeGreaterThan(0.6);
    }

    [Fact]
    public void WordSimilarity_IsLow_ForAnUnrelatedName()
    {
        Fuzzy.WordSimilarity("Region Hovedstaden", "What is the population of Denmark?").Should().BeLessThan(0.3);
    }

    [Fact]
    public void Fold_SpellsOutDanishLetters()
    {
        Fuzzy.Fold("København Ærø Ålborg").Should().Be("kobenhavn aero aalborg");
    }
}
```

Create `tests/Application.Tests/Assistant/Quick/ParameterResolverTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Application.Statistics;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class ParameterResolverTests
{
    private readonly IQueryDispatcher _queries = Substitute.For<IQueryDispatcher>();

    private ParameterResolver Resolver => new(_queries);

    private void AreasAre(params (string Code, string Name)[] areas) =>
        _queries.SendAsync(Arg.Any<GetPopulationAreas>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PopulationAreasView([.. areas.Select(a => new PopulationAreaView(a.Code, a.Name))], "Statistics Denmark")));

    [Fact]
    public void OrderId_ReadsAFullGuid()
    {
        var id = Guid.NewGuid();

        ParameterResolver.OrderId($"has order {id} shipped?").Should().Be(id);
    }

    [Fact]
    public void OrderId_IsNull_WithoutOne()
    {
        ParameterResolver.OrderId("has my last order shipped?").Should().BeNull();
    }

    [Fact]
    public void OrderPrefix_ReadsEightHexDigits()
    {
        ParameterResolver.OrderPrefix("status of order 3F2A1B9C please").Should().Be("3f2a1b9c");
    }

    [Fact]
    public void OrderPrefix_IsNull_WhenAFullGuidIsPresent()
    {
        ParameterResolver.OrderPrefix($"order {Guid.NewGuid()}").Should().BeNull();
    }

    [Theory]
    [InlineData("did anything fail in the last hour?", 1)]
    [InlineData("any failed jobs today?", 24)]
    [InlineData("failures in the last 6 hours", 6)]
    [InlineData("failures in the last 3 days", 72)]
    [InlineData("failures this week", 168)]
    [InlineData("failures in the last 900 hours", 168)]
    [InlineData("any failures?", 12)]
    public void Window_ReadsTheTimeSpan_OrFallsBack(string question, int hours)
    {
        ParameterResolver.Window(question, TimeSpan.FromHours(12)).Should().Be(TimeSpan.FromHours(hours));
    }

    [Fact]
    public async Task AreaAsync_TakesAThreeDigitCodeAsIs()
    {
        var result = await Resolver.AreaAsync("population of area 101", CancellationToken.None);

        result.Value.Area!.Code.Should().Be("101");
        await _queries.DidNotReceiveWithAnyArgs().SendAsync<PopulationAreasView>(default!, default);
    }

    [Fact]
    public async Task AreaAsync_MatchesAnAreaName()
    {
        AreasAre(("000", "All Denmark"), ("084", "Region Hovedstaden"), ("101", "Copenhagen"));

        var result = await Resolver.AreaAsync("How many people live in Copenhagen?", CancellationToken.None);

        result.Value.Area.Should().Be(new PopulationAreaView("101", "Copenhagen"));
    }

    [Fact]
    public async Task AreaAsync_ReadsDenmarkAsTheWholeCountry()
    {
        AreasAre(("000", "All Denmark"), ("084", "Region Hovedstaden"), ("101", "Copenhagen"));

        var result = await Resolver.AreaAsync("What is the population of Denmark?", CancellationToken.None);

        result.Value.Area!.Code.Should().Be("000");
    }

    [Fact]
    public async Task AreaAsync_OffersCandidates_WhenTwoNamesAreTooClose()
    {
        // "lyngbi" scores 0.556 against Lyngby and 0.500 against Lyngbye: both match, 0.056 apart,
        // under the 0.1 lead an answer needs.
        AreasAre(("173", "Lyngby"), ("174", "Lyngbye"));

        var result = await Resolver.AreaAsync("population of Lyngbi", CancellationToken.None);

        result.Value.Area.Should().BeNull();
        result.Value.Candidates.Select(c => c.Code).Should().BeEquivalentTo(["173", "174"]);
    }

    [Fact]
    public async Task AreaAsync_FindsNothing_ForAnUnknownPlace()
    {
        AreasAre(("101", "Copenhagen"));

        var result = await Resolver.AreaAsync("population of Atlantis", CancellationToken.None);

        result.Value.Area.Should().BeNull();
        result.Value.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task AreaAsync_PassesOnAFailureToReadTheAreas()
    {
        _queries.SendAsync(Arg.Any<GetPopulationAreas>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PopulationAreasView>(new Error(ErrorKind.Unavailable, "statistics.unavailable", "Down.")));

        var result = await Resolver.AreaAsync("population of Copenhagen", CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }
}
```

Create `tests/Application.Tests/Products/FindProductsHandlerTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Products;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Products;

public sealed class FindProductsHandlerTests
{
    private readonly IProductRepository _products = Substitute.For<IProductRepository>();

    private FindProductsHandler Handler => new(_products);

    [Fact]
    public async Task HandleAsync_PassesTheTextAndItsSkuShapedWords()
    {
        _products.FindAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ProductMatch>>([]));

        await Handler.HandleAsync(new FindProducts("price of sku-1042 or Widget", 5), CancellationToken.None);

        await _products.Received(1).FindAsync(
            "price of sku-1042 or Widget",
            Arg.Is<IReadOnlyList<string>>(skus => skus.Contains("SKU-1042") && skus.Contains("WIDGET")),
            FindProductsHandler.MinimumSimilarity,
            5,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("widget", 0)]
    [InlineData("widget", 11)]
    public async Task HandleAsync_RefusesBadInput(string text, int limit)
    {
        var result = await Handler.HandleAsync(new FindProducts(text, limit), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }
}
```

(The repository's own tests in Step 5 pin the SQL; these pin only what the handler passes.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Application.Tests --filter "FuzzyTests|ParameterResolverTests|FindProductsHandlerTests" --nologo`
Expected: compile failure (`Fuzzy`, `ParameterResolver`, `FindProducts` not found).

- [ ] **Step 3: Write the Application code**

Create `src/Application/Assistant/Quick/Fuzzy.cs`:

```csharp
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

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Word();
}
```

Create `src/Application/Assistant/Quick/ParameterResolver.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Statistics;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>An area, or the candidates when two names were too close to call, or neither.</summary>
public sealed record AreaResolution(PopulationAreaView? Area, IReadOnlyList<PopulationAreaView> Candidates);

/// <summary>
/// Reads an intent's parameters from the question with rules, never generation: a value it cannot
/// resolve is reported as missing, and the caller asks instead of guessing. ADR 0036.
/// </summary>
public sealed partial class ParameterResolver(IQueryDispatcher queries)
{
    public const double AreaMatch = 0.5;

    public const double AreaLead = 0.1;

    public static Guid? OrderId(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return FullGuid().Match(question) is { Success: true } match ? Guid.Parse(match.Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The first eight hex digits of an order id, as the order list shows them.</summary>
    public static string? OrderPrefix(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (FullGuid().IsMatch(question))
        {
            return null;
        }

        return ShortId().Match(question) is { Success: true } match ? match.Value.ToLowerInvariant() : null;
    }

    /// <summary>"6 hours", "3 days", "hour", "today", "week", or <paramref name="fallback"/>; at most the tools' 168 hours.</summary>
    public static TimeSpan Window(string question, TimeSpan fallback)
    {
        ArgumentNullException.ThrowIfNull(question);

        var lower = question.ToLowerInvariant();
        if (Hours().Match(lower) is { Success: true } hours)
        {
            return Clamp(int.Parse(hours.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        if (Days().Match(lower) is { Success: true } days)
        {
            return Clamp(int.Parse(days.Groups[1].Value, CultureInfo.InvariantCulture) * 24);
        }

        if (lower.Contains("hour", StringComparison.Ordinal))
        {
            return TimeSpan.FromHours(1);
        }

        if (lower.Contains("week", StringComparison.Ordinal))
        {
            return TimeSpan.FromHours(AssistantToolbox.MaxWindowHours);
        }

        return lower.Contains("today", StringComparison.Ordinal) || lower.Contains("day", StringComparison.Ordinal)
            ? TimeSpan.FromHours(24)
            : fallback;
    }

    public async Task<Result<AreaResolution>> AreaAsync(string question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (AreaCode().Match(question) is { Success: true } code)
        {
            return Result.Success(new AreaResolution(new PopulationAreaView(code.Value, code.Value), []));
        }

        var areas = await queries.SendAsync(new GetPopulationAreas(), cancellationToken).ConfigureAwait(false);
        if (!areas.IsSuccess)
        {
            return Result.Failure<AreaResolution>(areas.Error);
        }

        var ranked = areas.Value.Areas
            .Select(area => (Area: area, Score: Fuzzy.WordSimilarity(area.Name, question)))
            .Where(r => r.Score >= AreaMatch)
            .OrderByDescending(r => r.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            return Result.Success(new AreaResolution(null, []));
        }

        if (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= AreaLead)
        {
            return Result.Success(new AreaResolution(ranked[0].Area, []));
        }

        return Result.Success(new AreaResolution(null, [.. ranked.Take(IntentRouter.MaxSuggestions).Select(r => r.Area)]));
    }

    private static TimeSpan Clamp(int hours) => TimeSpan.FromHours(Math.Clamp(hours, 1, AssistantToolbox.MaxWindowHours));

    [GeneratedRegex("\\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\b")]
    private static partial Regex FullGuid();

    [GeneratedRegex("\\b[0-9a-fA-F]{8}\\b")]
    private static partial Regex ShortId();

    [GeneratedRegex("\\b(\\d{1,4})\\s*(?:hours?|hrs?|h)\\b")]
    private static partial Regex Hours();

    [GeneratedRegex("\\b(\\d{1,3})\\s*days?\\b")]
    private static partial Regex Days();

    [GeneratedRegex("\\b\\d{3}\\b")]
    private static partial Regex AreaCode();
}
```

(`Window` checks "hour" before "today"/"day", so "in the last hour" is one hour; a question with both reads as one hour. `AreaCode` runs before the area lookup, so "area 101" needs no external call.)

Create `src/Application/Products/FindProducts.cs`:

```csharp
using System.Text.RegularExpressions;
using AiFramework.Application.Abstractions;
using AiFramework.Domain.Products;

namespace AiFramework.Application.Products;

/// <summary>A product found in free text, and how sure: 1 for a sku match, else pg_trgm word similarity.</summary>
public sealed record ProductMatch(Guid Id, string Sku, string Name, decimal Price, double Score);

/// <summary>
/// Products whose name appears, possibly misspelt, in <paramref name="Text"/>, or whose sku is one
/// of its words. Best first. The catalogue is global, so there is no owner to scope by. ADR 0036.
/// </summary>
public sealed record FindProducts(string Text, int Limit) : IQuery<IReadOnlyList<ProductMatch>>;

/// <summary>Validates its own inputs, as every query does (QueryDispatcher runs no validation behavior).</summary>
public sealed partial class FindProductsHandler(IProductRepository products)
    : IQueryHandler<FindProducts, IReadOnlyList<ProductMatch>>
{
    public const int MaxLimit = 10;

    public const int MaxTextLength = 2000;

    /// <summary>Below this, a name is not in the text. Tuned with the evaluation set's product questions.</summary>
    public const double MinimumSimilarity = 0.5;

    public async Task<Result<IReadOnlyList<ProductMatch>>> HandleAsync(FindProducts query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.Text) || query.Text.Length > MaxTextLength)
        {
            return Result.Failure<IReadOnlyList<ProductMatch>>(new Error(
                ErrorKind.Validation, "products.search_text", $"Search text is 1 to {MaxTextLength} characters."));
        }

        if (query.Limit is < 1 or > MaxLimit)
        {
            return Result.Failure<IReadOnlyList<ProductMatch>>(new Error(
                ErrorKind.Validation, "products.limit_out_of_range", $"Limit must be between 1 and {MaxLimit}."));
        }

        IReadOnlyList<string> skus = [.. SkuShaped().Matches(query.Text).Select(m => Product.NormalizeSku(m.Value)).Distinct(StringComparer.Ordinal).Take(20)];
        var matches = await products.FindAsync(query.Text, skus, MinimumSimilarity, query.Limit, cancellationToken).ConfigureAwait(false);
        return Result.Success(matches);
    }

    [GeneratedRegex("[A-Za-z0-9][A-Za-z0-9_-]*")]
    private static partial Regex SkuShaped();
}
```

In `src/Application/Products/IProductRepository.cs`, add to the interface:

```csharp
    /// <summary>
    /// Products whose name appears in <paramref name="text"/> with at least
    /// <paramref name="minimumSimilarity"/> (pg_trgm word similarity), or whose normalised sku is one
    /// of <paramref name="skus"/> (score 1). Best first, no tracking.
    /// </summary>
    public Task<IReadOnlyList<ProductMatch>> FindAsync(
        string text, IReadOnlyList<string> skus, double minimumSimilarity, int limit, CancellationToken cancellationToken);
```

- [ ] **Step 4: Run the Application tests to verify they pass**

Run: `dotnet test tests/Application.Tests --filter "FuzzyTests|ParameterResolverTests|FindProductsHandlerTests" --nologo`
Expected: all passed. (Infrastructure does not compile yet: `ProductRepository` lacks `FindAsync`. That is Step 6.)

- [ ] **Step 5: Write the failing repository test**

Create `tests/Infrastructure.Tests/Persistence/ProductRepositoryFindTests.cs`:

```csharp
using AiFramework.Domain.Products;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Persistence;

[Collection(nameof(PostgresCollection))]
public sealed class ProductRepositoryFindTests(PostgresFixture fixture)
{
    // The catalogue is shared by every test in the collection, so each test invents names no other
    // test uses: a made-up word plus a per-test suffix of letters (digits would split the word).
    private static string Unique() => new([.. Guid.NewGuid().ToString("N").Where(char.IsLetter).Take(6)]);

    private async Task<Product> SeedAsync(string name, string sku, decimal price = 19.95m)
    {
        var product = Product.Create(Guid.NewGuid(), sku, name, null, price, DateTimeOffset.UtcNow);
        await using var context = fixture.CreateContext();
        context.Products.Add(product);
        await context.SaveChangesAsync();
        return product;
    }

    private async Task<IReadOnlyList<AiFramework.Application.Products.ProductMatch>> FindAsync(string text, params string[] skus)
    {
        await using var context = fixture.CreateContext();
        return await new ProductRepository(context).FindAsync(text, skus, 0.5, 5, CancellationToken.None);
    }

    [Fact]
    public async Task FindAsync_FindsANameInsideAQuestion()
    {
        var word = $"Quorblax{Unique()}";
        var product = await SeedAsync(word, $"Q-{Unique()}".ToUpperInvariant());

        var found = await FindAsync($"how much is the {word.ToLowerInvariant()}?");

        found.Should().ContainSingle(m => m.Id == product.Id).Which.Score.Should().BeApproximately(1, 0.001);
    }

    [Fact]
    public async Task FindAsync_FindsAMisspeltName()
    {
        var suffix = Unique();
        var product = await SeedAsync($"Zentrofax {suffix}", $"Z-{Unique()}".ToUpperInvariant());

        var found = await FindAsync($"price of the zentrofx {suffix}");

        found.Should().Contain(m => m.Id == product.Id);
    }

    [Fact]
    public async Task FindAsync_FindsBySku()
    {
        var sku = $"SKU-{Unique()}".ToUpperInvariant();
        var product = await SeedAsync($"Plain {Unique()}", sku);

        var found = await FindAsync("what is this one", sku);

        found.Should().ContainSingle(m => m.Id == product.Id).Which.Score.Should().Be(1);
    }

    [Fact]
    public async Task FindAsync_FindsNothing_ForAnUnrelatedQuestion()
    {
        await SeedAsync($"Vextraplume {Unique()}", $"V-{Unique()}".ToUpperInvariant());

        var found = await FindAsync("write a poem about the rain");

        found.Should().NotContain(m => m.Name.StartsWith("Vextraplume", StringComparison.Ordinal));
    }
}
```

(If `PostgresFixture.CreateContext()` or `AiFrameworkDbContext.Products` are named differently, `ProductRepositoryTests.cs` shows the names; match them.)

Run: `dotnet test tests/Infrastructure.Tests --filter ProductRepositoryFindTests --nologo`
Expected: compile failure, `ProductRepository` does not implement `FindAsync`.

- [ ] **Step 6: Implement the search and enable pg_trgm**

In `src/Infrastructure/Persistence/ProductRepository.cs`, add `using AiFramework.Application.Products;` (already present) and:

```csharp
    /// <summary>
    /// word_similarity finds a product's name inside a whole question ("how much is the widjet?"),
    /// which plain similarity, comparing whole strings, cannot. The function form uses no index; a
    /// catalogue of thousands is a sequential scan of milliseconds. Revisit with the &lt;% operator
    /// and a GIN trigram index past roughly 10,000 products. ADR 0036.
    /// </summary>
    public async Task<IReadOnlyList<ProductMatch>> FindAsync(
        string text, IReadOnlyList<string> skus, double minimumSimilarity, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(skus);

        var skuArray = skus.ToArray();
        var rows = await context.Database.SqlQuery<ProductMatchRow>($"""
            SELECT p."Id", p."Sku", p."Name", p."Price",
                   (CASE WHEN p."Sku" = ANY({skuArray}) THEN 1.0
                         ELSE word_similarity(lower(p."Name"), lower({text})) END)::double precision AS "Score"
            FROM products p
            WHERE p."Sku" = ANY({skuArray})
               OR word_similarity(lower(p."Name"), lower({text})) >= {minimumSimilarity}
            ORDER BY "Score" DESC, p."Name"
            LIMIT {limit}
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. rows.Select(r => new ProductMatch(r.Id, r.Sku, r.Name, r.Price, r.Score))];
    }

    /// <summary>The raw query's row; EF maps it by property name.</summary>
    private sealed class ProductMatchRow
    {
        public Guid Id { get; set; }

        public string Sku { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public decimal Price { get; set; }

        public double Score { get; set; }
    }
```

In `src/Infrastructure/Persistence/AiFrameworkDbContext.cs`, at the top of `OnModelCreating` (before `ApplyConfigurationsFromAssembly`):

```csharp
        // Trigram similarity for finding a product by name in a question (FindProducts, ADR 0036).
        // pg_trgm ships with the standard Postgres images and is a trusted extension, so the
        // database owner may create it without being a superuser.
        modelBuilder.HasPostgresExtension("pg_trgm");
```

- [ ] **Step 7: Generate the migration**

Run (bash):

```bash
dotnet tool restore
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=x;Password=y' \
  dotnet ef migrations add EnablePgTrgm \
  --project src/Infrastructure --startup-project src/Infrastructure --output-dir Persistence/Migrations
```

Expected: a new `*_EnablePgTrgm.cs` whose `Up` contains `migrationBuilder.AlterDatabase().Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");` and nothing else. If it contains any other change, stop: the model drifted from the snapshot, and that is a separate problem.

- [ ] **Step 8: Register the query**

In `src/Infrastructure/InfrastructureRegistration.cs`, after `services.AddQuery<GetProducts, ProductPage, GetProductsHandler>();`:

```csharp
        services.AddQuery<FindProducts, IReadOnlyList<ProductMatch>, FindProductsHandler>();
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test tests/Infrastructure.Tests --filter "ProductRepositoryFindTests|RegistrationCompleteness" --nologo`
Expected: all passed (the fixture migrates its database, so `pg_trgm` is enabled by the new migration).

- [ ] **Step 10: Commit**

```bash
git add src/Application/Assistant/Quick src/Application/Products src/Infrastructure/Persistence src/Infrastructure/InfrastructureRegistration.cs tests/Application.Tests tests/Infrastructure.Tests/Persistence/ProductRepositoryFindTests.cs
git commit -m "feat(assistant): resolve orders, time windows, areas and products from a question

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Composing an answer

**Files:**
- Create: `src/Application/Assistant/Quick/QuickText.cs`, `src/Application/Assistant/Quick/QuickAnswerComposer.cs`
- Test: `tests/Application.Tests/Assistant/Quick/QuickAnswerComposerTests.cs`

**Interfaces:**
- Consumes: `IQueryDispatcher`; `GetOrder`/`OrderView`, `GetOrders`/`OrderPage`/`OrderListItem`, `GetOrderExports`/`OrderExportView`/`OrderExportState`, `FindProducts`/`ProductMatch`, `GetPopulation`/`PopulationView`, `GetJobHealth`/`JobHealthView`, `GetJobRuns`/`JobRunPage`/`JobRunView`, `GetDeadLetters`/`DeadLetterPage`/`DeadLetterView`, `GetTrafficSummary`/`TrafficSummaryView`, `GetExternalSystemStatus`/`ExternalSystemsView`/`ExternalSystemRowView`, `GetSignInHealth`/`SignInHealthView`; `ParameterResolver` (Task 6); `QuickIntent`, `QuickAnswer`, `QuickSuggestions`, `HelpSection` (Task 3).
- Produces: `QuickAnswerComposer(IQueryDispatcher, ParameterResolver)`; `ComposeAsync(QuickIntent intent, string question, string? answeredAs, CancellationToken) : Task<QuickReply>`; `static Help(HelpSection section, string? answeredAs) : QuickAnswer`; `QuickText.Date/Money/Count/Rate/Percent/Milliseconds/ShortId/Window`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Application.Tests/Assistant/Quick/QuickAnswerComposerTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Application.Monitoring;
using AiFramework.Application.Orders;
using AiFramework.Application.Products;
using AiFramework.Application.Statistics;
using AiFramework.Domain.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class QuickAnswerComposerTests
{
    private static readonly DateTimeOffset Placed = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Shipped = new(2026, 10, 3, 14, 12, 0, TimeSpan.Zero);
    private static readonly Guid OrderId = Guid.Parse("3f2a1b9c-0000-4000-8000-000000000001");

    private readonly IQueryDispatcher _queries = Substitute.For<IQueryDispatcher>();

    private QuickAnswerComposer Composer => new(_queries, new ParameterResolver(_queries));

    private static QuickIntent Intent(string id) => QuickIntents.Find(id) ?? throw new InvalidOperationException(id);

    private Task<QuickReply> ComposeAsync(string intent, string question) =>
        Composer.ComposeAsync(Intent(intent), question, answeredAs: null, CancellationToken.None);

    private static OrderView Order(OrderStatus status) => new(
        OrderId, "SKU-1042", 3, Placed, Guid.NewGuid(), "Widget Pro", 19.95m, status,
        status == OrderStatus.Shipped ? Shipped : null, status == OrderStatus.Cancelled ? Shipped : null);

    private void NewestOrderIs(OrderView order)
    {
        _queries.SendAsync(Arg.Is<GetOrders>(q => q.Limit == 1), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderPage([new OrderListItem(order.Id, order.Sku, order.Quantity, order.PlacedAt, order.ProductId, order.ProductName, order.UnitPrice, order.Status)], null)));
        _queries.SendAsync(Arg.Is<GetOrder>(q => q.Id == order.Id), Arg.Any<CancellationToken>())
            .Returns(Result.Success(order));
    }

    [Fact]
    public async Task OrderStatus_ForAShippedLastOrder_SaysWhenItShipped_AndLinksToIt()
    {
        NewestOrderIs(Order(OrderStatus.Shipped));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, "Has my last order shipped?");

        answer.Text.Should().Be("Your last order shipped on 3 Oct 2026, 14:12 UTC.");
        answer.Facts.Should().Contain(new QuickFact("Product", "Widget Pro (SKU-1042)"))
            .And.Contain(new QuickFact("Quantity", "3 × 19.95"))
            .And.Contain(new QuickFact("Status", "Shipped"));
        answer.Link.Should().Be(new QuickLink("Open order", $"/orders/{OrderId}"));
        answer.Kind.Should().Be(QuickAnswerKind.Instant);
    }

    [Fact]
    public async Task OrderStatus_ForAPlacedOrder_SaysItHasNotShipped()
    {
        NewestOrderIs(Order(OrderStatus.Placed));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, "Has my last order shipped?");

        answer.Text.Should().Be("Your last order has not shipped yet. It was placed on 1 Oct 2026, 09:30 UTC.");
    }

    [Fact]
    public async Task OrderStatus_ByFullId_ReadsThatOrder()
    {
        _queries.SendAsync(Arg.Is<GetOrder>(q => q.Id == OrderId), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Order(OrderStatus.Cancelled)));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, $"what happened to order {OrderId}");

        answer.Text.Should().Be("Order 3f2a1b9c was cancelled on 3 Oct 2026, 14:12 UTC.");
    }

    [Fact]
    public async Task OrderStatus_ForAnOrderThatIsNotTheCallers_SaysItWasNotFound()
    {
        _queries.SendAsync(Arg.Any<GetOrder>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<OrderView>(new Error(ErrorKind.NotFound, "orders.not_found", "No such order.")));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, $"status of order {OrderId}");

        answer.Text.Should().Be("I couldn't find order 3f2a1b9c among your orders.");
        answer.Link.Should().Be(new QuickLink("All orders", "/orders"));
    }

    [Fact]
    public async Task OrderStatus_WithNoOrders_SaysSo_AndLinksToPlaceOne()
    {
        _queries.SendAsync(Arg.Any<GetOrders>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new OrderPage([], null)));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, "Has my last order shipped?");

        answer.Text.Should().Be("You haven't placed any orders yet.");
        answer.Link!.To.Should().Be("/orders/new");
    }

    [Fact]
    public async Task OrderStatus_WhenTheOrdersCannotBeRead_SaysSo_WithoutGuessing()
    {
        _queries.SendAsync(Arg.Any<GetOrders>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<OrderPage>(new Error(ErrorKind.Unavailable, "db.down", "Down.")));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.OrderStatus, "Has my last order shipped?");

        answer.Text.Should().Be("Your orders couldn't be loaded right now. Try again in a moment.");
        answer.Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task RecentOrders_ListsThemInATable()
    {
        _queries.SendAsync(Arg.Is<GetOrders>(q => q.Limit == QuickAnswerComposer.RecentOrderCount), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new OrderPage([new OrderListItem(OrderId, "SKU-1042", 3, Placed, null, "Widget Pro", 19.95m, OrderStatus.Placed)], null)));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.RecentOrders, "Show my recent orders");

        answer.Text.Should().Be("Your most recent order:");
        answer.Table!.Columns.Should().Equal("Order", "Product", "Quantity", "Status", "Placed");
        answer.Table.Rows.Should().ContainSingle().Which.Should().Equal("3f2a1b9c", "Widget Pro", "3", "Placed", "1 Oct 2026, 09:30 UTC");
        answer.Link!.To.Should().Be("/orders");
    }

    [Fact]
    public async Task ExportStatus_ForAReadyExport_GivesTheRowCount()
    {
        _queries.SendAsync(Arg.Any<GetOrderExports>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<OrderExportView>>([new OrderExportView(Guid.NewGuid(), OrderExportState.Ready, Placed, Shipped, 42)]));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.ExportStatus, "Is my export ready?");

        answer.Text.Should().Be("Your latest export is ready: 42 orders, finished on 3 Oct 2026, 14:12 UTC.");
        answer.Link!.To.Should().Be("/orders/exports");
    }

    [Fact]
    public async Task ProductInfo_ForOneClearMatch_GivesItsPrice()
    {
        var id = Guid.NewGuid();
        _queries.SendAsync(Arg.Any<FindProducts>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ProductMatch>>([new ProductMatch(id, "SKU-1042", "Widget Pro", 19.95m, 1)]));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.ProductInfo, "how much is the widget pro");

        answer.Text.Should().Be("Widget Pro costs 19.95.");
        answer.Link.Should().Be(new QuickLink("Open product", $"/products/{id}"));
    }

    [Fact]
    public async Task ProductInfo_ForACloseMatch_SaysItIsTheClosest()
    {
        _queries.SendAsync(Arg.Any<FindProducts>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ProductMatch>>([new ProductMatch(Guid.NewGuid(), "SKU-1042", "Widget Pro", 19.95m, 0.7)]));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.ProductInfo, "how much is the widjet pro");

        answer.Text.Should().Be("Widget Pro, the closest match, costs 19.95.");
    }

    [Fact]
    public async Task ProductInfo_ForTwoSimilarProducts_AsksWhichOne_NeverPicks()
    {
        _queries.SendAsync(Arg.Any<FindProducts>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ProductMatch>>(
            [
                new ProductMatch(Guid.NewGuid(), "W-1", "Widget", 9.95m, 0.8),
                new ProductMatch(Guid.NewGuid(), "W-2", "Widget Pro", 19.95m, 0.75),
            ]));

        var reply = await ComposeAsync(QuickIntents.ProductInfo, "price of the widgt");

        var suggestions = reply.Should().BeOfType<QuickSuggestions>().Subject;
        suggestions.Text.Should().Be("Which product did you mean?");
        suggestions.Options.Should().Equal("How much is Widget?", "How much is Widget Pro?");
    }

    [Fact]
    public async Task Population_ForAResolvedArea_GivesTheFigure()
    {
        _queries.SendAsync(Arg.Any<GetPopulationAreas>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PopulationAreasView([new PopulationAreaView("101", "Copenhagen")], "Statistics Denmark")));
        _queries.SendAsync(Arg.Is<GetPopulation>(q => q.Area == "101"), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PopulationView("101", "Copenhagen", "2026Q3", 667099, "Statistics Denmark")));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.Population, "population of Copenhagen");

        answer.Text.Should().Be("Copenhagen had 667,099 inhabitants in 2026Q3.");
        answer.Link!.To.Should().Be("/statistics/population");
    }

    [Fact]
    public async Task JobHealth_WithNoFailures_SaysSo()
    {
        _queries.SendAsync(Arg.Is<GetJobHealth>(q => q.Window == TimeSpan.FromHours(24)), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new JobHealthView(1, 40, 0, 0, Placed)));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.JobHealth, "Did any background job fail today?");

        answer.Text.Should().Be("No background job failed in the last 24 hours.");
        answer.Link!.To.Should().Be("/monitoring/jobs");
    }

    [Fact]
    public async Task FailedJobs_ShowsNoErrorText()
    {
        var run = new JobRunView(Guid.NewGuid(), 2, "SendOrderConfirmation", null, JobRunStatus.Failed, Placed, Shipped, 120, null, "secret detail", null, null);
        _queries.SendAsync(Arg.Any<GetJobRuns>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new JobRunPage([run], 7, 1)));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.FailedJobs, "Which jobs failed?");

        answer.Table!.Rows.SelectMany(r => r).Should().NotContain(cell => cell.Contains("secret", StringComparison.Ordinal));
        answer.Text.Should().Be("The most recent failed job run, of 7:");
    }

    [Fact]
    public async Task Traffic_FormatsTheErrorRateAsAPercentage()
    {
        var overall = new TrafficRowView(default, "all", 600, 6, 0, null, 20, 80, 120);
        _queries.SendAsync(Arg.Any<GetTrafficSummary>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TrafficSummaryView(Placed, 10, 0.01, overall, [])));

        var answer = (QuickAnswer)await ComposeAsync(QuickIntents.Traffic, "How is the API doing?");

        answer.Text.Should().Be("In the last hour the API served 10 requests a minute, with an error rate of 1.0%.");
        answer.Facts.Should().Contain(new QuickFact("p95 latency", "80 ms"));
    }

    [Fact]
    public void Help_QuotesTheSection_AndLinksToItsScreen()
    {
        var section = new HelpSection("export-orders", "Exporting your orders", "How do I export my orders as a PDF?", "Request an export", "Open Exports and choose Export my orders.", new QuickLink("Go to Exports", "/orders/exports"));

        var answer = QuickAnswerComposer.Help(section, answeredAs: null);

        answer.Kind.Should().Be(QuickAnswerKind.Help);
        answer.Title.Should().Be("Exporting your orders");
        answer.Text.Should().Be("Open Exports and choose Export my orders.");
        answer.Link.Should().Be(section.Link);
        answer.Facts.Should().BeEmpty();
        answer.Table.Should().BeNull();
    }

    [Fact]
    public void EveryIntent_HasAComposer()
    {
        foreach (var intent in QuickIntents.All)
        {
            var act = () => Composer.ComposeAsync(intent, "x", null, CancellationToken.None);

            act.Should().NotThrowAsync<InvalidOperationException>($"{intent.Id} needs a case in ComposeAsync");
        }
    }
}
```

(`EveryIntent_HasAComposer` runs every intent against a substitute that returns default `Result`s; the substitute's default for a `Task<Result<T>>` is a completed task holding `null`, which the composer must treat like any other failed read. If that dereferences null, give the composer a guard rather than weakening the test: `if (result is null || !result.IsSuccess)`. Read `JobRunView`, `TrafficRowView` and `OrderExportView`'s constructors before running: the positional arguments above follow the fields listed in this plan's research; if a field was added since, add it.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Application.Tests --filter QuickAnswerComposerTests --nologo`
Expected: compile failure, `QuickAnswerComposer` and `QuickText` not found.

- [ ] **Step 3: Write the formatting helpers**

Create `src/Application/Assistant/Quick/QuickText.cs`:

```csharp
using System.Globalization;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// How figures read in an answer. Invariant culture throughout: the chiseled runtime image has no
/// ICU, and an answer must read the same on every pod. Times are UTC and say so.
/// </summary>
public static class QuickText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Date(DateTimeOffset at) => at.UtcDateTime.ToString("d MMM yyyy, HH:mm 'UTC'", Invariant);

    public static string Money(decimal amount) => amount.ToString("0.00", Invariant);

    public static string Count(long count) => count.ToString("N0", Invariant);

    public static string Rate(double perMinute) => perMinute.ToString("0.#", Invariant);

    /// <summary>A fraction (0.0123) as a percentage ("1.2%").</summary>
    public static string Percent(double fraction) => fraction.ToString("0.0%", Invariant);

    public static string Milliseconds(double? ms) => ms is { } value ? $"{value.ToString("0", Invariant)} ms" : "no data";

    /// <summary>The first eight hex digits of an id, as the order list shows them.</summary>
    public static string ShortId(Guid id) => id.ToString("N", Invariant)[..8];

    public static string Window(TimeSpan window) => window.TotalHours switch
    {
        1d => "the last hour",
        24d => "the last 24 hours",
        168d => "the last 7 days",
        var hours when hours % 24 == 0 => $"the last {((int)hours / 24).ToString(Invariant)} days",
        var hours => $"the last {((int)hours).ToString(Invariant)} hours",
    };
}
```

- [ ] **Step 4: Write the composer**

Create `src/Application/Assistant/Quick/QuickAnswerComposer.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Application.Orders;
using AiFramework.Application.Products;
using AiFramework.Application.Statistics;
using AiFramework.Domain.Orders;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// One method per intent: dispatch the same caller-scoped queries the AI's tools use, then build
/// the answer from their values and nothing else. A failed read is said plainly, never filled in. A
/// name too close to call is asked about, never picked. Monitoring answers carry counts and names
/// only — no error text — so nothing free-form reaches a replay. ADR 0036.
/// </summary>
public sealed class QuickAnswerComposer(IQueryDispatcher queries, ParameterResolver parameters)
{
    public const int RecentOrderCount = 5;

    public const int TableRows = 5;

    public const int OrderPrefixScan = 25;

    public const int ProductCandidates = 5;

    /// <summary>How far the best product must lead the next to be named without asking.</summary>
    public const double ProductLead = 0.1;

    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(15);

    private static readonly QuickLink AllOrders = new("All orders", "/orders");
    private static readonly QuickLink PlaceOrder = new("Place an order", "/orders/new");
    private static readonly QuickLink Exports = new("Go to Exports", "/orders/exports");
    private static readonly QuickLink Catalogue = new("Open the catalogue", "/products");
    private static readonly QuickLink PopulationPage = new("Population figures", "/statistics/population");
    private static readonly QuickLink JobRuns = new("Job runs", "/monitoring/jobs");
    private static readonly QuickLink TrafficPage = new("Traffic", "/monitoring/traffic");
    private static readonly QuickLink IntegrationsPage = new("External systems", "/monitoring/integrations");
    private static readonly QuickLink SignInsPage = new("Sign-ins", "/monitoring/logins");

    public static QuickAnswer Help(HelpSection section, string? answeredAs)
    {
        ArgumentNullException.ThrowIfNull(section);
        return new QuickAnswer(QuickAnswerKind.Help, section.Title, section.Text, [], null, section.Link, answeredAs);
    }

    public async Task<QuickReply> ComposeAsync(QuickIntent intent, string question, string? answeredAs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(question);

        QuickReply reply = intent.Id switch
        {
            QuickIntents.OrderStatus => await OrderStatusAsync(question, cancellationToken).ConfigureAwait(false),
            QuickIntents.RecentOrders => await RecentOrdersAsync(cancellationToken).ConfigureAwait(false),
            QuickIntents.ExportStatus => await ExportStatusAsync(cancellationToken).ConfigureAwait(false),
            QuickIntents.ProductInfo => await ProductAsync(question, cancellationToken).ConfigureAwait(false),
            QuickIntents.Population => await PopulationAsync(question, cancellationToken).ConfigureAwait(false),
            QuickIntents.JobHealth => await JobHealthAsync(question, cancellationToken).ConfigureAwait(false),
            QuickIntents.FailedJobs => await FailedJobsAsync(cancellationToken).ConfigureAwait(false),
            QuickIntents.DeadLetters => await DeadLettersAsync(cancellationToken).ConfigureAwait(false),
            QuickIntents.Traffic => await TrafficAsync(question, cancellationToken).ConfigureAwait(false),
            QuickIntents.Integrations => await IntegrationsAsync(cancellationToken).ConfigureAwait(false),
            QuickIntents.SignIns => await SignInsAsync(question, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"No composer for intent '{intent.Id}'."),
        };

        return reply is QuickAnswer answer ? answer with { AnsweredAs = answeredAs } : reply;
    }

    private static QuickAnswer Answer(string text, IReadOnlyList<QuickFact> facts, QuickTable? table, QuickLink link) =>
        new(QuickAnswerKind.Instant, null, text, facts, table, link, null);

    private static QuickAnswer CouldNotLoad(string what, QuickLink link) =>
        Answer($"{what} couldn't be loaded right now. Try again in a moment.", [], null, link);

    private static bool Failed<T>(Result<T>? result) => result is null || !result.IsSuccess;

    private async Task<QuickReply> OrderStatusAsync(string question, CancellationToken ct)
    {
        var reference = ParameterResolver.OrderId(question);
        if (reference is null && ParameterResolver.OrderPrefix(question) is { } prefix)
        {
            var recent = await queries.SendAsync(new GetOrders(OrderPrefixScan, null), ct).ConfigureAwait(false);
            if (Failed(recent))
            {
                return CouldNotLoad("Your orders", AllOrders);
            }

            var hit = recent.Value.Items.FirstOrDefault(o => QuickText.ShortId(o.Id) == prefix);
            if (hit is null)
            {
                return Answer($"I couldn't find order {prefix} among your orders.", [], null, AllOrders);
            }

            reference = hit.Id;
        }

        string subject;
        if (reference is { } explicitId)
        {
            subject = $"Order {QuickText.ShortId(explicitId)}";
        }
        else
        {
            var newest = await queries.SendAsync(new GetOrders(1, null), ct).ConfigureAwait(false);
            if (Failed(newest))
            {
                return CouldNotLoad("Your orders", AllOrders);
            }

            if (newest.Value.Items.Count == 0)
            {
                return Answer("You haven't placed any orders yet.", [], null, PlaceOrder);
            }

            reference = newest.Value.Items[0].Id;
            subject = "Your last order";
        }

        var read = await queries.SendAsync(new GetOrder(reference.Value), ct).ConfigureAwait(false);
        if (Failed(read))
        {
            return read?.Error.Kind == ErrorKind.NotFound
                ? Answer($"I couldn't find order {QuickText.ShortId(reference.Value)} among your orders.", [], null, AllOrders)
                : CouldNotLoad("Your order", AllOrders);
        }

        var order = read.Value;
        var text = order.Status switch
        {
            OrderStatus.Shipped => $"{subject} shipped on {QuickText.Date(order.ShippedAt ?? order.PlacedAt)}.",
            OrderStatus.Cancelled => $"{subject} was cancelled on {QuickText.Date(order.CancelledAt ?? order.PlacedAt)}.",
            _ => $"{subject} has not shipped yet. It was placed on {QuickText.Date(order.PlacedAt)}.",
        };

        return Answer(
            text,
            [
                new("Product", order.ProductName is { } name ? $"{name} ({order.Sku})" : order.Sku),
                new("Quantity", order.UnitPrice is { } price ? $"{QuickText.Count(order.Quantity)} × {QuickText.Money(price)}" : QuickText.Count(order.Quantity)),
                new("Status", order.Status.ToString()),
                new("Placed", QuickText.Date(order.PlacedAt)),
            ],
            null,
            new QuickLink("Open order", $"/orders/{order.Id}"));
    }

    private async Task<QuickReply> RecentOrdersAsync(CancellationToken ct)
    {
        var page = await queries.SendAsync(new GetOrders(RecentOrderCount, null), ct).ConfigureAwait(false);
        if (Failed(page))
        {
            return CouldNotLoad("Your orders", AllOrders);
        }

        var orders = page.Value.Items;
        if (orders.Count == 0)
        {
            return Answer("You haven't placed any orders yet.", [], null, PlaceOrder);
        }

        var table = new QuickTable(
            ["Order", "Product", "Quantity", "Status", "Placed"],
            [.. orders.Select(o => (IReadOnlyList<string>)[QuickText.ShortId(o.Id), o.ProductName ?? o.Sku, QuickText.Count(o.Quantity), o.Status.ToString(), QuickText.Date(o.PlacedAt)])]);

        var text = orders.Count == 1 ? "Your most recent order:" : $"Your {QuickText.Count(orders.Count)} most recent orders:";
        return Answer(text, [], table, AllOrders);
    }

    private async Task<QuickReply> ExportStatusAsync(CancellationToken ct)
    {
        var exports = await queries.SendAsync(new GetOrderExports(), ct).ConfigureAwait(false);
        if (Failed(exports))
        {
            return CouldNotLoad("Your exports", Exports);
        }

        if (exports.Value.Count == 0)
        {
            return Answer("You haven't requested an export yet.", [], null, Exports);
        }

        var latest = exports.Value.MaxBy(e => e.RequestedAt) ?? exports.Value[0];
        var text = latest.Status switch
        {
            OrderExportState.Ready => $"Your latest export is ready: {QuickText.Count(latest.RowCount ?? 0)} orders, finished on {QuickText.Date(latest.CompletedAt ?? latest.RequestedAt)}.",
            OrderExportState.Failed => "Your latest export failed. Request a new one from Exports.",
            _ => $"Your latest export is still being built. It was requested on {QuickText.Date(latest.RequestedAt)}, and you'll get a notification when it's ready.",
        };

        var facts = new List<QuickFact> { new("Requested", QuickText.Date(latest.RequestedAt)) };
        if (latest.CompletedAt is { } completed)
        {
            facts.Add(new("Finished", QuickText.Date(completed)));
        }

        if (latest.RowCount is { } rows)
        {
            facts.Add(new("Orders", QuickText.Count(rows)));
        }

        return Answer(text, facts, null, Exports);
    }

    private async Task<QuickReply> ProductAsync(string question, CancellationToken ct)
    {
        var found = await queries.SendAsync(new FindProducts(question, ProductCandidates), ct).ConfigureAwait(false);
        if (Failed(found))
        {
            return CouldNotLoad("The catalogue", Catalogue);
        }

        var matches = found.Value;
        if (matches.Count == 0)
        {
            return Answer("I couldn't find that product in the catalogue. Try its exact name or SKU.", [], null, Catalogue);
        }

        var best = matches[0];
        var exact = best.Score >= 0.999;
        if (!exact && matches.Count > 1 && best.Score - matches[1].Score < ProductLead)
        {
            return new QuickSuggestions(
                "Which product did you mean?",
                [.. matches.Take(IntentRouter.MaxSuggestions).Select(m => $"How much is {m.Name}?")]);
        }

        var name = exact ? best.Name : $"{best.Name}, the closest match,";
        return Answer(
            $"{name} costs {QuickText.Money(best.Price)}.",
            [new("Name", best.Name), new("SKU", best.Sku), new("Price", QuickText.Money(best.Price))],
            null,
            new QuickLink("Open product", $"/products/{best.Id}"));
    }

    private async Task<QuickReply> PopulationAsync(string question, CancellationToken ct)
    {
        var resolved = await parameters.AreaAsync(question, ct).ConfigureAwait(false);
        if (Failed(resolved))
        {
            return CouldNotLoad("Population figures", PopulationPage);
        }

        if (resolved.Value.Area is not { } area)
        {
            return resolved.Value.Candidates.Count > 1
                ? new QuickSuggestions("Which area did you mean?", [.. resolved.Value.Candidates.Select(c => $"What is the population of {c.Name}?")])
                : Answer("Which area? Ask, for example, \"What is the population of Copenhagen?\"", [], null, PopulationPage);
        }

        var figures = await queries.SendAsync(new GetPopulation(area.Code), ct).ConfigureAwait(false);
        if (Failed(figures))
        {
            return CouldNotLoad("Population figures", PopulationPage);
        }

        var p = figures.Value;
        return Answer(
            $"{p.AreaName} had {QuickText.Count(p.Population)} inhabitants in {p.Period}.",
            [new("Area", p.AreaName), new("Period", p.Period), new("Population", QuickText.Count(p.Population)), new("Source", p.Source)],
            null,
            PopulationPage);
    }

    private async Task<QuickReply> JobHealthAsync(string question, CancellationToken ct)
    {
        var window = ParameterResolver.Window(question, TimeSpan.FromHours(24));
        var health = await queries.SendAsync(new GetJobHealth(window), ct).ConfigureAwait(false);
        if (Failed(health))
        {
            return CouldNotLoad("Job health", JobRuns);
        }

        var h = health.Value;
        var span = QuickText.Window(window);
        var text = h.Failed + h.DeadLettered == 0
            ? $"No background job failed in {span}."
            : $"In {span}, {QuickText.Count(h.Failed)} job runs failed and {QuickText.Count(h.DeadLettered)} were dead-lettered.";

        return Answer(
            text,
            [new("Running", QuickText.Count(h.Running)), new("Succeeded", QuickText.Count(h.Succeeded)), new("Failed", QuickText.Count(h.Failed)), new("Dead-lettered", QuickText.Count(h.DeadLettered))],
            null,
            JobRuns);
    }

    private async Task<QuickReply> FailedJobsAsync(CancellationToken ct)
    {
        var runs = await queries.SendAsync(new GetJobRuns(JobRunStatus.Failed, null, 1, TableRows), ct).ConfigureAwait(false);
        if (Failed(runs))
        {
            return CouldNotLoad("Job runs", JobRuns);
        }

        var page = runs.Value;
        if (page.Items.Count == 0)
        {
            return Answer("No job runs have failed.", [], null, JobRuns);
        }

        var table = new QuickTable(
            ["Job", "Attempt", "Started"],
            [.. page.Items.Select(r => (IReadOnlyList<string>)[r.JobName, QuickText.Count(r.Attempt), QuickText.Date(r.StartedAt)])]);
        var text = page.Items.Count == 1
            ? $"The most recent failed job run, of {QuickText.Count(page.TotalCount)}:"
            : $"The {QuickText.Count(page.Items.Count)} most recent failed job runs, of {QuickText.Count(page.TotalCount)}:";

        return Answer(text, [], table, JobRuns);
    }

    private async Task<QuickReply> DeadLettersAsync(CancellationToken ct)
    {
        var letters = await queries.SendAsync(new GetDeadLetters(1, TableRows), ct).ConfigureAwait(false);
        if (Failed(letters))
        {
            return CouldNotLoad("Dead letters", JobRuns);
        }

        var page = letters.Value;
        if (page.Items.Count == 0)
        {
            return Answer("Nothing is dead-lettered.", [], null, JobRuns);
        }

        var table = new QuickTable(
            ["Message", "Exception", "Sent", "Replayable"],
            [.. page.Items.Select(d => (IReadOnlyList<string>)[d.MessageType, d.ExceptionType, QuickText.Date(d.SentAt), d.Replayable ? "Yes" : "No"])]);

        return Answer($"{QuickText.Count(page.TotalCount)} dead-lettered messages. The most recent:", [], table, JobRuns);
    }

    private async Task<QuickReply> TrafficAsync(string question, CancellationToken ct)
    {
        var window = ParameterResolver.Window(question, TimeSpan.FromHours(1));
        var summary = await queries.SendAsync(new GetTrafficSummary(window), ct).ConfigureAwait(false);
        if (Failed(summary))
        {
            return CouldNotLoad("Traffic", TrafficPage);
        }

        var t = summary.Value;
        return Answer(
            $"In {QuickText.Window(window)} the API served {QuickText.Rate(t.RequestsPerMinute)} requests a minute, with an error rate of {QuickText.Percent(t.ErrorRate)}.",
            [
                new("Requests", QuickText.Count(t.Overall.Total)),
                new("Requests a minute", QuickText.Rate(t.RequestsPerMinute)),
                new("Error rate", QuickText.Percent(t.ErrorRate)),
                new("p95 latency", QuickText.Milliseconds(t.Overall.P95Ms)),
            ],
            null,
            TrafficPage);
    }

    private async Task<QuickReply> IntegrationsAsync(CancellationToken ct)
    {
        var status = await queries.SendAsync(new GetExternalSystemStatus(), ct).ConfigureAwait(false);
        if (Failed(status))
        {
            return CouldNotLoad("External system status", IntegrationsPage);
        }

        var systems = status.Value.Systems;
        if (systems.Count == 0)
        {
            return Answer("No external systems are configured.", [], null, IntegrationsPage);
        }

        var attention = systems.Count(s => s.Stale || s.State is not ExternalSystemState.Healthy);
        var text = attention == 0
            ? $"All {QuickText.Count(systems.Count)} external systems are healthy."
            : $"{QuickText.Count(attention)} of {QuickText.Count(systems.Count)} external systems need attention.";
        var table = new QuickTable(
            ["System", "State", "Checked"],
            [.. systems.Select(s => (IReadOnlyList<string>)[
                s.Name,
                s.Stale ? "Stale" : s.State?.ToString() ?? "Not checked",
                s.CheckedAt is { } at ? QuickText.Date(at) : "Never"])]);

        return Answer(text, [], table, IntegrationsPage);
    }

    private async Task<QuickReply> SignInsAsync(string question, CancellationToken ct)
    {
        var window = ParameterResolver.Window(question, TimeSpan.FromHours(24));
        var health = await queries.SendAsync(new GetSignInHealth(window, ActiveWindow), ct).ConfigureAwait(false);
        if (Failed(health))
        {
            return CouldNotLoad("Sign-in health", SignInsPage);
        }

        // Counts only: LockedOutUsers names accounts, and an answer never does.
        var s = health.Value;
        return Answer(
            $"In {QuickText.Window(window)}: {QuickText.Count(s.Succeeded)} successful sign-ins, {QuickText.Count(s.BadCredentials + s.UnknownUser)} failed, {QuickText.Count(s.LockedOut)} refused by a lockout. {QuickText.Count(s.LockedOutUsers.Count)} accounts are locked out now.",
            [
                new("Successful", QuickText.Count(s.Succeeded)),
                new("Failed", QuickText.Count(s.BadCredentials + s.UnknownUser)),
                new("Locked out now", QuickText.Count(s.LockedOutUsers.Count)),
                new("Active now", QuickText.Count(s.Active.Count)),
            ],
            null,
            SignInsPage);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Application.Tests --filter QuickAnswerComposerTests --nologo`
Expected: all passed. A culture-dependent mismatch (for example `1.0%` printed as `1,0 %`) means a value was formatted without `QuickText`; fix the formatting, not the expected string.

- [ ] **Step 6: Commit**

```bash
git add src/Application/Assistant/Quick tests/Application.Tests/Assistant/Quick
git commit -m "feat(assistant): compose instant answers from the caller's own queries

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Two new turn types, stored, displayed and replayed

**Files:**
- Create: `src/Application/Assistant/Quick/QuickReplay.cs`
- Modify: `src/Application/Assistant/AssistantTranscript.cs`, `src/Application/Assistant/AssistantHistory.cs` (`AssistantDisplayMessage`), `src/Infrastructure/Assistant/AssistantTurnCodec.cs`, `src/Infrastructure/Assistant/AnthropicRequestMapper.cs`, `src/Infrastructure/Assistant/AssistantDisplay.cs`
- Test: `tests/Application.Tests/Assistant/Quick/QuickReplayTests.cs`, `tests/Infrastructure.Tests/Assistant/AssistantTurnCodecTests.cs`, `tests/Infrastructure.Tests/Assistant/ClaudeAssistantModelTests.cs` (one test added), `tests/Infrastructure.Tests/Assistant/AssistantDisplayTests.cs`

**Interfaces:**
- Consumes: `QuickAnswer`, `QuickSuggestions`, `QuickReply` (Task 3).
- Produces: `record QuickAnswerTurn(QuickAnswer Answer, string Replay) : AssistantTurn`; `record SuggestionsTurn(QuickSuggestions Suggestions, string Replay) : AssistantTurn`; `QuickReplay.For(QuickAnswer)`, `QuickReplay.For(QuickSuggestions)`, `QuickReplay.Turn(QuickReply) : AssistantTurn`; codec kinds `AssistantTurnCodec.Quick = "quick_answer"`, `AssistantTurnCodec.Suggested = "suggestions"`; `AssistantDisplayMessage(string Role, string Text, IReadOnlyList<string> Tools, QuickAnswer? Answer = null, QuickSuggestions? Suggestions = null)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Application.Tests/Assistant/Quick/QuickReplayTests.cs`:

```csharp
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class QuickReplayTests
{
    private static readonly QuickAnswer Answer = new(
        QuickAnswerKind.Instant, null, "Your last order shipped on 3 Oct 2026, 14:12 UTC.",
        [new QuickFact("Product", "Widget Pro (SKU-1042)")],
        new QuickTable(["Order", "Status"], [["3f2a1b9c", "Shipped"]]),
        new QuickLink("Open order", "/orders/3f2a1b9c-0000-4000-8000-000000000001"),
        null);

    [Fact]
    public void For_AnAnswer_SpellsOutEveryFigureAndTheLink()
    {
        QuickReplay.For(Answer).Should().Be(
            "Your last order shipped on 3 Oct 2026, 14:12 UTC.\n" +
            "Product: Widget Pro (SKU-1042)\n" +
            "Order: 3f2a1b9c; Status: Shipped\n" +
            "(Linked: Open order, /orders/3f2a1b9c-0000-4000-8000-000000000001)");
    }

    [Fact]
    public void For_Suggestions_ListsThem()
    {
        QuickReplay.For(new QuickSuggestions("I'm not sure what you mean. Did you mean one of these?", ["Show my recent orders", "Is my export ready?"]))
            .Should().Be("I'm not sure what you mean. Did you mean one of these? Suggested: Show my recent orders; Is my export ready?");
    }

    [Fact]
    public void Turn_FixesTheReplayAtWriteTime()
    {
        var turn = QuickReplay.Turn(Answer).Should().BeOfType<QuickAnswerTurn>().Subject;

        turn.Replay.Should().Be(QuickReplay.For(Answer));
        turn.Answer.Should().BeSameAs(Answer);
    }
}
```

Create `tests/Infrastructure.Tests/Assistant/AssistantTurnCodecTests.cs`:

```csharp
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Infrastructure.Assistant;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Assistant;

public sealed class AssistantTurnCodecTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QuickAnswerTurn_RoundTrips_WithItsReplayUnchanged()
    {
        var answer = new QuickAnswer(
            QuickAnswerKind.Help, "Exporting your orders", "Open Exports.", [new QuickFact("A", "B")],
            new QuickTable(["C"], [["D"]]), new QuickLink("Go to Exports", "/orders/exports"), "how do I export");
        var turn = new QuickAnswerTurn(answer, "replay, byte for byte");

        var row = AssistantTurnCodec.Encode(Guid.NewGuid(), 1, turn, Now);
        var decoded = AssistantTurnCodec.Decode(row).Should().BeOfType<QuickAnswerTurn>().Subject;

        row.Kind.Should().Be(AssistantTurnCodec.Quick);
        decoded.Replay.Should().Be("replay, byte for byte");
        decoded.Answer.Text.Should().Be("Open Exports.");
        decoded.Answer.Kind.Should().Be(QuickAnswerKind.Help);
        decoded.Answer.Facts.Should().Equal(new QuickFact("A", "B"));
        decoded.Answer.Table!.Rows[0].Should().Equal("D");
        decoded.Answer.Link.Should().Be(new QuickLink("Go to Exports", "/orders/exports"));
        decoded.Answer.AnsweredAs.Should().Be("how do I export");
    }

    [Fact]
    public void SuggestionsTurn_RoundTrips()
    {
        var turn = new SuggestionsTurn(new QuickSuggestions("Not sure.", ["Is my export ready?"]), "Not sure. Suggested: Is my export ready?");

        var decoded = AssistantTurnCodec.Decode(AssistantTurnCodec.Encode(Guid.NewGuid(), 1, turn, Now))
            .Should().BeOfType<SuggestionsTurn>().Subject;

        decoded.Suggestions.Options.Should().Equal("Is my export ready?");
        decoded.Replay.Should().Be("Not sure. Suggested: Is my export ready?");
    }
}
```

Add to `tests/Infrastructure.Tests/Assistant/ClaudeAssistantModelTests.cs` (add `using AiFramework.Application.Assistant.Quick;`):

```csharp
    [Fact]
    public async Task NextTurnAsync_ReplaysQuickTurnsAsPlainAssistantText_InAlternation()
    {
        _fake.Enqueue(FakeReply.Text("ok"));
        var answer = new QuickAnswer(QuickAnswerKind.Instant, null, "Shipped.", [], null, null, null);
        var suggestions = new QuickSuggestions("Not sure.", ["Is my export ready?"]);

        await NextAsync(Request(
            true,
            new UserQuestionTurn("Has my last order shipped?", Now),
            new QuickAnswerTurn(answer, "Shipped.\n(Linked: Open order, /orders/x)"),
            new UserQuestionTurn("why", Now),
            new SuggestionsTurn(suggestions, "Not sure. Suggested: Is my export ready?"),
            new UserQuestionTurn("why", Now)));

        var messages = _fake.Requests.Should().ContainSingle().Subject.Body.GetProperty("messages");
        messages.EnumerateArray().Select(m => m.GetProperty("role").GetString())
            .Should().Equal("user", "assistant", "user", "assistant", "user");
        messages[1].GetProperty("content").GetString().Should().Be("Shipped.\n(Linked: Open order, /orders/x)");
        messages[3].GetProperty("content").GetString().Should().Be("Not sure. Suggested: Is my export ready?");
    }
```

Create `tests/Infrastructure.Tests/Assistant/AssistantDisplayTests.cs`:

```csharp
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Infrastructure.Assistant;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Assistant;

public sealed class AssistantDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_ShowsAQuickAnswerAsAnAssistantMessageCarryingTheCard()
    {
        var conversation = Guid.NewGuid();
        var answer = new QuickAnswer(QuickAnswerKind.Instant, null, "Shipped.", [], null, new QuickLink("Open order", "/orders"), null);
        var rows = new[]
        {
            AssistantTurnCodec.Encode(conversation, 0, new UserQuestionTurn("Has it shipped?", Now), Now),
            AssistantTurnCodec.Encode(conversation, 1, new QuickAnswerTurn(answer, "Shipped."), Now),
            AssistantTurnCodec.Encode(conversation, 2, new UserQuestionTurn("why", Now), Now),
            AssistantTurnCodec.Encode(conversation, 3, new SuggestionsTurn(new QuickSuggestions("Not sure.", ["Is my export ready?"]), "Not sure."), Now),
        };

        var messages = AssistantDisplay.Build(rows);

        messages.Should().HaveCount(4);
        messages[1].Role.Should().Be(AssistantDisplayMessage.AssistantRole);
        messages[1].Text.Should().Be("Shipped.");
        messages[1].Answer!.Link!.To.Should().Be("/orders");
        messages[3].Suggestions!.Options.Should().Equal("Is my export ready?");
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Application.Tests --filter QuickReplayTests --nologo` and `dotnet test tests/Infrastructure.Tests --filter "AssistantTurnCodecTests|AssistantDisplayTests|NextTurnAsync_ReplaysQuickTurns" --nologo`
Expected: compile failure (`QuickAnswerTurn`, `QuickReplay`, `AssistantTurnCodec.Quick` not found).

- [ ] **Step 3: Add the turn types and the replay**

In `src/Application/Assistant/AssistantTranscript.cs`, add `using AiFramework.Application.Assistant.Quick;` and, after `ToolResultsTurn`:

```csharp
/// <summary>
/// An instant or help answer exactly as shown, and <paramref name="Replay"/>, the text Claude is
/// given for it if the user later asks the AI. Both are fixed when the turn is written: rendering
/// either again from a template would change history, which Claude rejects (ADR 0033, ADR 0036).
/// </summary>
public sealed record QuickAnswerTurn(QuickAnswer Answer, string Replay) : AssistantTurn;

/// <summary>The suggestions offered when the router was not sure, and their fixed replay text.</summary>
public sealed record SuggestionsTurn(QuickSuggestions Suggestions, string Replay) : AssistantTurn;
```

Create `src/Application/Assistant/Quick/QuickReplay.cs`:

```csharp
using System.Text;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>
/// The plain text Claude is given for a quick reply when the user later asks the AI in the same
/// conversation: the answer's sentence, every figure, and the screen it linked to. Built once, when
/// the turn is written, and never again. ADR 0036.
/// </summary>
public static class QuickReplay
{
    public static string For(QuickAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var text = new StringBuilder(answer.Text);
        foreach (var fact in answer.Facts)
        {
            text.Append('\n').Append(fact.Label).Append(": ").Append(fact.Value);
        }

        if (answer.Table is { } table)
        {
            foreach (var row in table.Rows)
            {
                text.Append('\n').AppendJoin("; ", table.Columns.Zip(row, (column, value) => $"{column}: {value}"));
            }
        }

        if (answer.Link is { } link)
        {
            text.Append("\n(Linked: ").Append(link.Label).Append(", ").Append(link.To).Append(')');
        }

        return text.ToString();
    }

    public static string For(QuickSuggestions suggestions)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        return suggestions.Options.Count == 0
            ? suggestions.Text
            : $"{suggestions.Text} Suggested: {string.Join("; ", suggestions.Options)}";
    }

    public static AssistantTurn Turn(QuickReply reply) => reply switch
    {
        QuickAnswer answer => new QuickAnswerTurn(answer, For(answer)),
        QuickSuggestions suggestions => new SuggestionsTurn(suggestions, For(suggestions)),
        _ => throw new ArgumentOutOfRangeException(nameof(reply), reply?.GetType().Name, "Unknown quick reply."),
    };
}
```

In `src/Application/Assistant/AssistantHistory.cs`, add `using AiFramework.Application.Assistant.Quick;` and change the record to:

```csharp
/// <summary>
/// One message as the page shows it: a question, or everything the model wrote for it with the
/// tools it used, or a quick reply with its card or suggestions. Built from the stored turns for
/// display only; it never goes back to the model.
/// </summary>
public sealed record AssistantDisplayMessage(
    string Role,
    string Text,
    IReadOnlyList<string> Tools,
    QuickAnswer? Answer = null,
    QuickSuggestions? Suggestions = null)
{
    public const string UserRole = "user";

    public const string AssistantRole = "assistant";
}
```

- [ ] **Step 4: Store, replay and display them**

In `src/Infrastructure/Assistant/AssistantTurnCodec.cs`, add `using AiFramework.Application.Assistant.Quick;`, the constants and cases:

```csharp
    public const string Quick = "quick_answer";
    public const string Suggested = "suggestions";
```

In `Encode`'s switch, before the `_` arm:

```csharp
            QuickAnswerTurn a => (Quick, JsonSerializer.Serialize(new StoredQuickAnswer(a.Answer, a.Replay), Json)),
            SuggestionsTurn s => (Suggested, JsonSerializer.Serialize(new StoredSuggestions(s.Suggestions, s.Replay), Json)),
```

In `Decode`'s switch, before the `_` arm:

```csharp
        Quick => DecodeQuick(row),
        Suggested => DecodeSuggestions(row),
```

And the helpers beside `DecodeQuestion`:

```csharp
    private static QuickAnswerTurn DecodeQuick(AssistantTurnRow row)
    {
        var stored = Deserialize<StoredQuickAnswer>(row);
        return new QuickAnswerTurn(stored.Answer, stored.Replay);
    }

    private static SuggestionsTurn DecodeSuggestions(AssistantTurnRow row)
    {
        var stored = Deserialize<StoredSuggestions>(row);
        return new SuggestionsTurn(stored.Suggestions, stored.Replay);
    }

    // QuickAnswerKind is stored as its number: append new kinds at the end, never reorder.
    private sealed record StoredQuickAnswer(QuickAnswer Answer, string Replay);

    private sealed record StoredSuggestions(QuickSuggestions Suggestions, string Replay);
```

In `src/Infrastructure/Assistant/AnthropicRequestMapper.cs`, in `ToMessage`'s switch, before the `_` arm:

```csharp
        // The stored replay text, verbatim: never re-rendered, so the history Claude sees never changes.
        QuickAnswerTurn a => new() { Role = Role.Assistant, Content = a.Replay },
        SuggestionsTurn s => new() { Role = Role.Assistant, Content = s.Replay },
```

In `src/Infrastructure/Assistant/AssistantDisplay.cs`, in `Build`'s switch, before `default:`:

```csharp
                case AssistantTurnCodec.Quick:
                    Flush(messages, ref text, ref tools);
                    var quick = (QuickAnswerTurn)AssistantTurnCodec.Decode(turn);
                    messages.Add(new AssistantDisplayMessage(AssistantDisplayMessage.AssistantRole, quick.Answer.Text, [], Answer: quick.Answer));
                    break;

                case AssistantTurnCodec.Suggested:
                    Flush(messages, ref text, ref tools);
                    var suggested = (SuggestionsTurn)AssistantTurnCodec.Decode(turn);
                    messages.Add(new AssistantDisplayMessage(AssistantDisplayMessage.AssistantRole, suggested.Suggestions.Text, [], Suggestions: suggested.Suggestions));
                    break;
```

(and add `using AiFramework.Application.Assistant.Quick;` if the compiler asks for it).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Application.Tests --filter "QuickReplayTests|Assistant" --nologo` and `dotnet test tests/Infrastructure.Tests --filter "Assistant" --nologo`
Expected: all passed, including every existing assistant test (history, retention, model). If `messages[1].GetProperty("content")` is an array rather than a string, the SDK serialises string content as a single text block: then assert on `messages[1].GetProperty("content")[0].GetProperty("text").GetString()` — the replay text must still match byte for byte.

- [ ] **Step 6: Commit**

```bash
git add src/Application/Assistant src/Infrastructure/Assistant tests/Application.Tests/Assistant tests/Infrastructure.Tests/Assistant
git commit -m "feat(assistant): store quick replies as turns that replay to Claude verbatim

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: The AskQuick command, and the output port

**Files:**
- Create: `src/Application/Assistant/AssistantConversationOpener.cs`, `src/Application/Assistant/Quick/AskQuick.cs`, `src/Api/Assistant/AssistantCards.cs`
- Modify: `src/Application/Assistant/AskAssistant.cs` (use the opener and the new limits), `src/Application/Assistant/AssistantPorts.cs` (two output methods), `src/Infrastructure/Assistant/NoAssistantOutput.cs`, `src/Api/Assistant/SseAssistantOutput.cs`, `src/Api/Assistant/AssistantDtos.cs`, `src/Api/Assistant/AssistantAvailability.cs`, `src/Api/Program.cs` (availability construction only), `src/Infrastructure/InfrastructureRegistration.cs`
- Test: `tests/Application.Tests/Assistant/Quick/AskQuickHandlerTests.cs`, `tests/Application.Tests/Assistant/AskAssistantHandlerTests.cs` (limits)

**Interfaces:**
- Consumes: `IntentRouter`, `QuickRouting`, `QuickIndexState` (Task 4); `QuickAnswerComposer` (Task 7); `QuickReplay` (Task 8).
- Produces:
  - `AssistantLimits.MaxQuestionsPerConversation = 50`, `MaxAiQuestionsPerConversation = 20`, `AiQuestions(IReadOnlyList<AssistantTurn>) : int`
  - `AssistantConversationOpener.OpenAsync(IAssistantConversations, Guid? conversationId, Guid ownerId, string question, DateTimeOffset now, bool forAi, CancellationToken) : Task<Result<AssistantConversation>>`
  - `record AskQuick(Guid? ConversationId, string Question, ToolScope Scope) : ICommand<AssistantAnswer>`; `AskQuickValidator`; `AskQuickHandler`; `AskQuickHandler.NotAvailable : QuickSuggestions`
  - `IAssistantOutput.AnswerAsync(QuickAnswer, CancellationToken)`, `IAssistantOutput.SuggestionsAsync(QuickSuggestions, CancellationToken)`
  - Api: `AssistantAvailability(bool AiEnabled, bool QuickEnabled, TimeSpan HeartbeatInterval)` with `AnyEnabled`; DTOs `AssistantAnswerResponse`, `AssistantFactResponse`, `AssistantTableResponse`, `AssistantLinkResponse`, `AssistantSuggestionsResponse`; `AssistantEventResponse.Answer`/`.Suggestions` and factories `Answered(...)`, `Suggested(...)`; `AssistantCards.ToResponse(QuickAnswer)`, `AssistantCards.ToResponse(QuickSuggestions, bool aiAvailable)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Application.Tests/Assistant/Quick/AskQuickHandlerTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Application.Orders;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class AskQuickHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Caller = Guid.NewGuid();
    private static readonly QuickRouting Routing = new(0.8, 0.05, 0.5);

    private readonly IAssistantConversations _conversations = Substitute.For<IAssistantConversations>();
    private readonly IAssistantOutput _output = Substitute.For<IAssistantOutput>();
    private readonly IQueryDispatcher _queries = Substitute.For<IQueryDispatcher>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly QuickIndexState _state = new();
    private readonly FakeEmbedder _embedder = new FakeEmbedder()
        .Add("Show my recent orders", 1, 0)
        .Add("Is my export ready?", 0, 1);

    public AskQuickHandlerTests()
    {
        _user.Id.Returns(Caller);
        _clock.UtcNow.Returns(Now);
        var catalog = new QuickCatalog(
            [new(QuickIntents.RecentOrders, "Show my recent orders"), new(QuickIntents.ExportStatus, "Is my export ready?")], []);
        var built = QuickIndexBuilder.Build(catalog, _embedder).Value;
        _state.Publish(built.Index, built.Spelling);
        _queries.SendAsync(Arg.Any<GetOrders>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new OrderPage([], null)));
    }

    private AskQuickHandler Handler => new(
        new IntentRouter(_state, _embedder, Routing),
        new QuickAnswerComposer(_queries, new ParameterResolver(_queries)),
        _conversations, _output, _user, _clock);

    private Task<Result<AssistantAnswer>> AskAsync(string question, Guid? conversation = null) =>
        Handler.HandleAsync(new AskQuick(conversation, question, ToolScope.Member), CancellationToken.None);

    // Questions here use only catalog words, so the spelling pass leaves them alone and the fake
    // embedder (which throws on an unknown text) sees exactly the text it was given.
    [Fact]
    public async Task AMatchedQuestion_IsAnsweredWrittenAndStored()
    {
        _embedder.Add("show my orders", 1, 0.05f);

        var result = await AskAsync("show my orders");

        result.IsSuccess.Should().BeTrue();
        await _output.Received(1).AnswerAsync(Arg.Is<QuickAnswer>(a => a.Text == "You haven't placed any orders yet."), Arg.Any<CancellationToken>());
        _conversations.Received(1).Start(result.Value.ConversationId, Caller, "show my orders", Now);
        _conversations.Received(1).Append(
            result.Value.ConversationId,
            Arg.Is<IReadOnlyList<AssistantTurn>>(turns => turns.Count == 2 && turns[0] is UserQuestionTurn && turns[1] is QuickAnswerTurn),
            Now);
    }

    [Fact]
    public async Task AnUnsureQuestion_GetsSuggestions_AndStoresThem()
    {
        _embedder.Add("orders or export", 1, 1);

        await AskAsync("orders or export");

        await _output.Received(1).SuggestionsAsync(
            Arg.Is<QuickSuggestions>(s => s.Options.Count == 2 && s.Text == "I'm not sure what you mean. Did you mean one of these?"),
            Arg.Any<CancellationToken>());
        _conversations.Received(1).Append(Arg.Any<Guid>(), Arg.Is<IReadOnlyList<AssistantTurn>>(t => t[1] is SuggestionsTurn), Now);
    }

    [Fact]
    public async Task WhenTheIndexIsNotReady_TheReplySaysSo_AndNothingIsGuessed()
    {
        var handler = new AskQuickHandler(
            new IntentRouter(new QuickIndexState(), _embedder, Routing),
            new QuickAnswerComposer(_queries, new ParameterResolver(_queries)),
            _conversations, _output, _user, _clock);

        await handler.HandleAsync(new AskQuick(null, "anything", ToolScope.Member), CancellationToken.None);

        await _output.Received(1).SuggestionsAsync(AskQuickHandler.NotAvailable, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFullConversation_IsRefused()
    {
        var id = Guid.NewGuid();
        _conversations.FindAsync(id, Arg.Any<CancellationToken>())
            .Returns(new AssistantConversation(id, Caller, AssistantLimits.MaxQuestionsPerConversation, []));

        var result = await AskAsync("Show my recent orders", id);

        result.Error.Code.Should().Be("assistant.conversation_full");
        await _output.DidNotReceiveWithAnyArgs().AnswerAsync(default!, default);
    }

    [Fact]
    public async Task AnotherUsersConversation_IsNotFound()
    {
        var id = Guid.NewGuid();
        _conversations.FindAsync(id, Arg.Any<CancellationToken>()).Returns(new AssistantConversation(id, Guid.NewGuid(), 1, []));

        var result = await AskAsync("Show my recent orders", id);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public void AiQuestions_CountsOnlyQuestionsTheModelAnswered()
    {
        IReadOnlyList<AssistantTurn> turns =
        [
            new UserQuestionTurn("a", Now), new ModelTurn("[]"),
            new UserQuestionTurn("b", Now), new QuickAnswerTurn(new QuickAnswer(QuickAnswerKind.Instant, null, "x", [], null, null, null), "x"),
            new UserQuestionTurn("c", Now), new ModelTurn("[]"), new ToolResultsTurn([]), new ModelTurn("[]"),
        ];

        AssistantLimits.AiQuestions(turns).Should().Be(2);
    }
}
```

In `tests/Application.Tests/Assistant/AskAssistantHandlerTests.cs`, find the test(s) that use `AskAssistantHandler.MaxQuestionsPerConversation` and change them to the new rule:

```csharp
    [Fact]
    public async Task HandleAsync_AConversationWithTwentyAiQuestions_IsRefused()
    {
        var id = Guid.NewGuid();
        var turns = Enumerable.Range(0, AssistantLimits.MaxAiQuestionsPerConversation)
            .SelectMany(i => new AssistantTurn[] { new UserQuestionTurn($"q{i}", Now), new ModelTurn("[]") })
            .ToList();
        _conversations.FindAsync(id, Arg.Any<CancellationToken>())
            .Returns(new AssistantConversation(id, Caller, AssistantLimits.MaxAiQuestionsPerConversation, turns));

        var result = await AskAsync("one more", id);

        result.Error.Code.Should().Be("assistant.conversation_full");
    }

    [Fact]
    public async Task HandleAsync_TwentyQuickQuestions_DoNotUseUpTheAiAllowance()
    {
        var id = Guid.NewGuid();
        var quick = new QuickAnswerTurn(new QuickAnswer(QuickAnswerKind.Instant, null, "x", [], null, null, null), "x");
        var turns = Enumerable.Range(0, 20)
            .SelectMany(i => new AssistantTurn[] { new UserQuestionTurn($"q{i}", Now), quick })
            .ToList();
        _conversations.FindAsync(id, Arg.Any<CancellationToken>()).Returns(new AssistantConversation(id, Caller, 20, turns));
        ModelReturns(Answer());

        var result = await AskAsync("now ask the AI", id);

        result.IsSuccess.Should().BeTrue();
    }
```

(`AskAsync`, `ModelReturns`, `Answer` and `_conversations` are the file's existing helpers; read it first and match their exact signatures. Add `using AiFramework.Application.Assistant.Quick;`.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Application.Tests --filter "AskQuickHandlerTests|AskAssistantHandlerTests" --nologo`
Expected: compile failure (`AskQuickHandler`, `AssistantLimits`, `IAssistantOutput.AnswerAsync` not found).

- [ ] **Step 3: Extract the opener and the limits**

Create `src/Application/Assistant/AssistantConversationOpener.cs`:

```csharp
using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Assistant;

/// <summary>
/// How many questions a conversation holds. The whole conversation is replayed to Claude whenever
/// the AI is asked, so the total is bounded too, not only the AI's share. ADR 0036.
/// </summary>
public static class AssistantLimits
{
    public const int MaxQuestionsPerConversation = 50;

    public const int MaxAiQuestionsPerConversation = 20;

    /// <summary>Questions the model answered: a question whose next turn is a model turn.</summary>
    public static int AiQuestions(IReadOnlyList<AssistantTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var count = 0;
        for (var i = 0; i + 1 < turns.Count; i++)
        {
            if (turns[i] is UserQuestionTurn && turns[i + 1] is ModelTurn)
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>Opens the caller's conversation for one more question, or starts one. Shared by AskAssistant and AskQuick.</summary>
public static class AssistantConversationOpener
{
    public static async Task<Result<AssistantConversation>> OpenAsync(
        IAssistantConversations conversations,
        Guid? conversationId,
        Guid ownerId,
        string question,
        DateTimeOffset now,
        bool forAi,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        if (conversationId is not { } id)
        {
            var started = new AssistantConversation(Guid.NewGuid(), ownerId, 0, []);
            conversations.Start(started.Id, ownerId, AssistantTitle.From(question), now);
            return Result.Success(started);
        }

        var existing = await conversations.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.OwnerId != ownerId)
        {
            return Result.Failure<AssistantConversation>(new Error(
                ErrorKind.NotFound, "assistant.conversation_not_found", "That conversation does not exist or has expired. Start a new one."));
        }

        if (existing.QuestionCount >= AssistantLimits.MaxQuestionsPerConversation)
        {
            return Result.Failure<AssistantConversation>(new Error(
                ErrorKind.Validation, "assistant.conversation_full", $"This conversation has reached {AssistantLimits.MaxQuestionsPerConversation} questions. Start a new one."));
        }

        if (forAi && AssistantLimits.AiQuestions(existing.Turns) >= AssistantLimits.MaxAiQuestionsPerConversation)
        {
            return Result.Failure<AssistantConversation>(new Error(
                ErrorKind.Validation, "assistant.conversation_full", $"This conversation has reached {AssistantLimits.MaxAiQuestionsPerConversation} questions for the AI. Start a new one to ask it more."));
        }

        return Result.Success(existing);
    }
}
```

In `src/Application/Assistant/AskAssistant.cs`: delete `MaxQuestionsPerConversation` and the private `OpenAsync`, and in `HandleAsync` replace the `OpenAsync(...)` call with:

```csharp
        var opened = await AssistantConversationOpener.OpenAsync(
            conversations, command.ConversationId, ownerId, command.Question, now, forAi: true, cancellationToken).ConfigureAwait(false);
```

Then search the solution for `AskAssistantHandler.MaxQuestionsPerConversation` (`rg -n "MaxQuestionsPerConversation" src tests frontend`) and point each use at `AssistantLimits.MaxAiQuestionsPerConversation`.

- [ ] **Step 4: Add the output methods and the command**

In `src/Application/Assistant/AssistantPorts.cs`, add `using AiFramework.Application.Assistant.Quick;` and to `IAssistantOutput`:

```csharp
    /// <summary>A quick answer, all at once: nothing to stream.</summary>
    public Task AnswerAsync(QuickAnswer answer, CancellationToken cancellationToken);

    public Task SuggestionsAsync(QuickSuggestions suggestions, CancellationToken cancellationToken);
```

In `src/Infrastructure/Assistant/NoAssistantOutput.cs`, implement both as `Task.CompletedTask`, in the style of its existing two members (add the `using`).

Create `src/Application/Assistant/Quick/AskQuick.cs`:

```csharp
using AiFramework.Application.Abstractions;
using FluentValidation;

namespace AiFramework.Application.Assistant.Quick;

/// <summary>One question for the instant path. A null conversation id starts a new conversation. ADR 0036.</summary>
public sealed record AskQuick(Guid? ConversationId, string Question, ToolScope Scope) : ICommand<AssistantAnswer>;

public sealed class AskQuickValidator : AbstractValidator<AskQuick>
{
    public AskQuickValidator()
    {
        RuleFor(c => c.Question)
            .NotEmpty()
            .Must(q => q.Trim().Length <= AskAssistantValidator.MaxQuestionLength)
            .WithMessage($"A question cannot be longer than {AskAssistantValidator.MaxQuestionLength} characters.");
    }
}

/// <summary>
/// Routes the question, answers it from the caller's own data or suggests, writes the reply, and
/// appends the question and the reply to the conversation as one transaction (the unit of work
/// commits after the command). Never calls a model and never dispatches a command. ADR 0036.
/// </summary>
public sealed class AskQuickHandler(
    IntentRouter router,
    QuickAnswerComposer composer,
    IAssistantConversations conversations,
    IAssistantOutput output,
    ICurrentUser currentUser,
    IClock clock) : ICommandHandler<AskQuick, AssistantAnswer>
{
    public static QuickSuggestions NotAvailable { get; } = new("Instant answers aren't available right now.", []);

    public async Task<Result<AssistantAnswer>> HandleAsync(AskQuick command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } ownerId)
        {
            return Result.Failure<AssistantAnswer>(new Error(ErrorKind.Unauthorized, "assistant.no_caller", "Sign in to use the assistant."));
        }

        var question = command.Question.Trim();
        var now = clock.UtcNow;
        var opened = await AssistantConversationOpener.OpenAsync(
            conversations, command.ConversationId, ownerId, question, now, forAi: false, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result.Failure<AssistantAnswer>(opened.Error);
        }

        var reply = await ReplyAsync(question, command.Scope, cancellationToken).ConfigureAwait(false);
        if (reply is QuickAnswer answer)
        {
            await output.AnswerAsync(answer, cancellationToken).ConfigureAwait(false);
        }
        else if (reply is QuickSuggestions suggestions)
        {
            await output.SuggestionsAsync(suggestions, cancellationToken).ConfigureAwait(false);
        }

        conversations.Append(opened.Value.Id, [new UserQuestionTurn(question, now), QuickReplay.Turn(reply)], now);
        return Result.Success(new AssistantAnswer(opened.Value.Id, AssistantOutcome.Answered));
    }

    private async Task<QuickReply> ReplyAsync(string question, ToolScope scope, CancellationToken cancellationToken)
    {
        var routed = router.Route(question, scope);
        if (!routed.IsSuccess)
        {
            return NotAvailable;
        }

        return routed.Value switch
        {
            RouteToIntent intent => await composer.ComposeAsync(intent.Intent, question, intent.AnsweredAs, cancellationToken).ConfigureAwait(false),
            RouteToHelp help => QuickAnswerComposer.Help(help.Section, help.AnsweredAs),
            RouteUnsure { Suggestions.Count: > 0 } unsure => new QuickSuggestions("I'm not sure what you mean. Did you mean one of these?", unsure.Suggestions),
            _ => new QuickSuggestions("I'm not sure what you mean.", []),
        };
    }
}
```

In `src/Infrastructure/InfrastructureRegistration.cs`, in `RegisterAssistant`, after the `AskAssistant` lines (add `using AiFramework.Application.Assistant.Quick;`):

```csharp
        // Instant answers (ADR 0036). Registered in every host, like AskAssistant, so the worker's
        // build-time validation sees a complete graph; only the API builds the index
        // (QuickRegistration.AddQuickAnswerIndexing), so in the worker the router reports unavailable.
        services.AddCommand<AskQuick, AssistantAnswer, AskQuickHandler>();
        services.AddScoped<IValidator<AskQuick>, AskQuickValidator>();
        services.AddScoped<IntentRouter>();
        services.AddScoped<QuickAnswerComposer>();
        services.AddScoped<ParameterResolver>();
        services.AddSingleton<QuickIndexState>();
        services.TryAddSingleton(QuickRouting.Default);
```

(`TryAddSingleton` needs `using Microsoft.Extensions.DependencyInjection.Extensions;`. The API registers its configured `QuickRouting` in Task 10, and the last registration wins.)

- [ ] **Step 5: The API's half of the port**

In `src/Api/Assistant/AssistantAvailability.cs`, replace the record:

```csharp
/// <summary>
/// Which halves of the assistant are on: the AI (Assistant__Enabled, which needs a key) and instant
/// answers (Assistant__Quick__Enabled, which needs only the local model), and how often the AI's
/// stream sends a keep-alive (Assistant__HeartbeatSeconds). Its own Api type so the controllers
/// never depend on Infrastructure's options (ArchitectureTests: DI only).
/// </summary>
public sealed record AssistantAvailability(bool AiEnabled, bool QuickEnabled, TimeSpan HeartbeatInterval)
{
    public bool AnyEnabled => AiEnabled || QuickEnabled;
}
```

In `src/Api/Program.cs`, change the `AssistantAvailability` construction to:

```csharp
builder.Services.AddSingleton(new AssistantAvailability(
    AiEnabled: assistantSection.GetValue<bool>("Enabled"),
    QuickEnabled: assistantSection.GetValue<bool>("Quick:Enabled"),
    HeartbeatInterval: TimeSpan.FromSeconds(assistantSection.GetValue("HeartbeatSeconds", defaultValue: 15))));
```

Then fix every `availability.Enabled` in `src/Api/Assistant/*.cs`: the AI endpoint (`Ask`) uses `AiEnabled`; the conversations controller (history list, read, rename, delete) uses `AnyEnabled`; `Status` is rewritten in Task 11 — for now `new AssistantStatusResponse { Enabled = availability.AiEnabled }`.

In `src/Api/Assistant/AssistantDtos.cs`, add (and `using AiFramework.Application.Assistant.Quick;`):

```csharp
public sealed record AssistantFactResponse
{
    public required string Label { get; init; }

    public required string Value { get; init; }
}

public sealed record AssistantTableResponse
{
    public required IReadOnlyList<string> Columns { get; init; }

    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }
}

/// <summary>An in-app path. The page renders it as a link only if it matches its allow-list.</summary>
public sealed record AssistantLinkResponse
{
    public required string Label { get; init; }

    public required string To { get; init; }
}

/// <summary>An instant or help answer, already formatted. Rendered as a card, never as Markdown.</summary>
public sealed record AssistantAnswerResponse
{
    public required QuickAnswerKind Kind { get; init; }

    public string? Title { get; init; }

    public required string Text { get; init; }

    public string? AnsweredAs { get; init; }

    public required IReadOnlyList<AssistantFactResponse> Facts { get; init; }

    public AssistantTableResponse? Table { get; init; }

    public AssistantLinkResponse? Link { get; init; }
}

/// <summary>What to offer when the router was not sure. AiAvailable says whether Ask the AI may be shown.</summary>
public sealed record AssistantSuggestionsResponse
{
    public required string Text { get; init; }

    public required IReadOnlyList<string> Options { get; init; }

    public required bool AiAvailable { get; init; }
}
```

and to `AssistantEventResponse` (update its summary to list `answer` and `suggestions`):

```csharp
    public AssistantAnswerResponse? Answer { get; init; }

    public AssistantSuggestionsResponse? Suggestions { get; init; }

    public static AssistantEventResponse Answered(AssistantAnswerResponse answer) => new() { Type = "answer", Answer = answer };

    public static AssistantEventResponse Suggested(AssistantSuggestionsResponse suggestions) => new() { Type = "suggestions", Suggestions = suggestions };
```

Create `src/Api/Assistant/AssistantCards.cs`:

```csharp
using AiFramework.Application.Assistant.Quick;

namespace AiFramework.Api.Assistant;

/// <summary>The one mapping from a quick reply to its wire shape, for the stream and for saved conversations.</summary>
public static class AssistantCards
{
    public static AssistantAnswerResponse ToResponse(QuickAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        return new AssistantAnswerResponse
        {
            Kind = answer.Kind,
            Title = answer.Title,
            Text = answer.Text,
            AnsweredAs = answer.AnsweredAs,
            Facts = [.. answer.Facts.Select(f => new AssistantFactResponse { Label = f.Label, Value = f.Value })],
            Table = answer.Table is { } table ? new AssistantTableResponse { Columns = table.Columns, Rows = table.Rows } : null,
            Link = answer.Link is { } link ? new AssistantLinkResponse { Label = link.Label, To = link.To } : null,
        };
    }

    public static AssistantSuggestionsResponse ToResponse(QuickSuggestions suggestions, bool aiAvailable)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        return new AssistantSuggestionsResponse { Text = suggestions.Text, Options = suggestions.Options, AiAvailable = aiAvailable };
    }
}
```

In `src/Api/Assistant/SseAssistantOutput.cs`, add `AssistantAvailability availability` as a third primary-constructor parameter and:

```csharp
    public Task AnswerAsync(QuickAnswer answer, CancellationToken cancellationToken) =>
        WriteAsync(AssistantEventResponse.Answered(AssistantCards.ToResponse(answer)), cancellationToken);

    public Task SuggestionsAsync(QuickSuggestions suggestions, CancellationToken cancellationToken) =>
        WriteAsync(AssistantEventResponse.Suggested(AssistantCards.ToResponse(suggestions, availability.AiEnabled)), cancellationToken);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet build --nologo -v q` — expected `0 Warning(s) 0 Error(s)`.
Run: `dotnet test tests/Application.Tests --nologo` and `dotnet test tests/Infrastructure.Tests --filter "Assistant|RegistrationCompleteness" --nologo`
Expected: all passed. `RegistrationCompletenessTests` proves `AskQuick`, its validator and `FindProducts` are registered.

- [ ] **Step 7: Commit**

```bash
git add src tests/Application.Tests
git commit -m "feat(assistant): answer a quick question and store it in the conversation

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Build the index when the API starts

**Files:**
- Create: `src/Infrastructure/Assistant/Quick/QuickCatalogLoader.cs`, `src/Infrastructure/Assistant/Quick/QuickRegistration.cs`
- Modify: `src/Api/Program.cs`, `src/Api/appsettings.json`, `src/Api/appsettings.Development.json`
- Test: `tests/Infrastructure.Tests/Assistant/Quick/QuickCatalogLoaderTests.cs`, `tests/Worker.IntegrationTests/QuickIndexNotInWorkerTests.cs`

**Interfaces:**
- Consumes: `QuickCatalogFiles.Load`, `QuickIndexBuilder.Build`, `QuickIndexState`, `ITextEmbedder`, `QuickRouting`, `QuickModelOptions`.
- Produces: `QuickCatalogLoader : IHostedService` (builds and publishes in `StartAsync`); `QuickRegistration.AddQuickAnswerIndexing(this IServiceCollection)`; configuration keys `Assistant:Quick:Enabled|ModelPath|Threshold|Margin|SuggestionFloor`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Infrastructure.Tests/Assistant/Quick/QuickCatalogLoaderTests.cs`:

```csharp
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Infrastructure.Assistant.Quick;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Assistant.Quick;

public sealed class QuickCatalogLoaderTests
{
    [Fact]
    public async Task StartAsync_PublishesAnIndexThatAnswersACanonicalQuestion()
    {
        using var embedder = new OnnxTextEmbedder(Options.Create(new QuickModelOptions()));
        var state = new QuickIndexState();

        await new QuickCatalogLoader(state, embedder, NullLogger<QuickCatalogLoader>.Instance).StartAsync(CancellationToken.None);

        state.IsReady.Should().BeTrue("the model must be fetched: run scripts/fetch-assistant-model.ps1");
        var router = new IntentRouter(state, embedder, QuickRouting.Default);
        router.Route("Has my last order shipped?", ToolScope.Member).Value
            .Should().BeOfType<RouteToIntent>().Which.Intent.Id.Should().Be(QuickIntents.OrderStatus);
    }

    [Fact]
    public async Task StartAsync_WithNoModel_LeavesTheIndexUnpublished_AndDoesNotThrow()
    {
        using var embedder = new OnnxTextEmbedder(Options.Create(new QuickModelOptions
        {
            ModelPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        }));
        var state = new QuickIndexState();

        await new QuickCatalogLoader(state, embedder, NullLogger<QuickCatalogLoader>.Instance).StartAsync(CancellationToken.None);

        state.IsReady.Should().BeFalse();
    }
}
```

Create `tests/Worker.IntegrationTests/QuickIndexNotInWorkerTests.cs`:

```csharp
using AiFramework.Application.Assistant.Quick;
using AiFramework.Infrastructure.Assistant.Quick;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiFramework.Worker.IntegrationTests;

/// <summary>The worker never answers a question, so it must never load the model (ADR 0036).</summary>
[Collection(nameof(WorkerFactoryCollection))]
public sealed class QuickIndexNotInWorkerTests(WorkerFactory factory)
{
    [Fact]
    public void Worker_RegistersNoQuickIndexLoader()
    {
        factory.Services.GetServices<IHostedService>().Should().NotContain(s => s is QuickCatalogLoader);
    }

    [Fact]
    public void Worker_NeverPublishesAQuickIndex()
    {
        factory.Services.GetRequiredService<QuickIndexState>().IsReady.Should().BeFalse();
    }
}
```

(Match the namespace and `WorkerFactory` usage to an existing class in `tests/Worker.IntegrationTests`, such as `OutboxPumpTests.cs`.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Infrastructure.Tests --filter QuickCatalogLoaderTests --nologo`
Expected: compile failure, `QuickCatalogLoader` not found.

- [ ] **Step 3: Write the loader and its registration**

Create `src/Infrastructure/Assistant/Quick/QuickCatalogLoader.cs`:

```csharp
using System.Diagnostics;
using AiFramework.Application.Assistant.Quick;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiFramework.Infrastructure.Assistant.Quick;

/// <summary>
/// Builds the instant-answer index while the API starts (about a second) and publishes it, so a
/// started API either answers instantly or knows it cannot. A missing or broken model is logged and
/// left unpublished, never thrown: instant answers then say they are unavailable and offer the AI,
/// and readiness is unaffected. Registered by the API only. ADR 0036.
/// </summary>
public sealed partial class QuickCatalogLoader(QuickIndexState state, ITextEmbedder embedder, ILogger<QuickCatalogLoader> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var built = QuickIndexBuilder.Build(QuickCatalogFiles.Load(), embedder);
        if (!built.IsSuccess)
        {
            LogUnavailable(logger, built.Error.Message);
            return Task.CompletedTask;
        }

        state.Publish(built.Value.Index, built.Value.Spelling);
        LogReady(logger, built.Value.Index.Count, built.Value.Spelling.VocabularySize, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Instant answers are unavailable: {Reason} Run scripts/fetch-assistant-model.ps1 to fetch the model.")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Instant answers ready: {Vectors} vectors, {Words} words, built in {Milliseconds:0} ms.")]
    private static partial void LogReady(ILogger logger, int vectors, int words, double milliseconds);
}
```

Create `src/Infrastructure/Assistant/Quick/QuickRegistration.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Assistant.Quick;

public static class QuickRegistration
{
    /// <summary>
    /// Builds the instant-answer index at start-up. Called by the API's Program.cs only, and only
    /// when Assistant:Quick:Enabled: the worker never answers questions and must never load the model.
    /// </summary>
    public static IServiceCollection AddQuickAnswerIndexing(this IServiceCollection services)
    {
        services.AddHostedService<QuickCatalogLoader>();
        return services;
    }
}
```

- [ ] **Step 4: Wire the API**

In `src/Api/Program.cs`, directly after the `AssistantAvailability` registration (add `using AiFramework.Application.Assistant.Quick;` and `using AiFramework.Infrastructure.Assistant.Quick;`):

```csharp
// Instant answers (ADR 0036): where the model is, how sure the router must be, and the index built
// while this host starts. HERE and not in AddInfrastructure: only the API answers questions, and the
// worker must never load the model. QuickRouting replaces AddInfrastructure's default (last wins).
var quickSection = assistantSection.GetSection("Quick");
builder.Services.AddOptions<QuickModelOptions>().Bind(quickSection);
builder.Services.AddSingleton(new QuickRouting(
    quickSection.GetValue("Threshold", QuickRouting.Default.Threshold),
    quickSection.GetValue("Margin", QuickRouting.Default.Margin),
    quickSection.GetValue("SuggestionFloor", QuickRouting.Default.SuggestionFloor)));
if (quickSection.GetValue<bool>("Enabled"))
{
    builder.Services.AddQuickAnswerIndexing();
}
```

In `src/Api/appsettings.json`, change the `Assistant` section to:

```json
  "Assistant": {
    "Enabled": false,
    "Quick": {
      "Enabled": false
    }
  },
```

In `src/Api/appsettings.Development.json`, add (merged with whatever is there; the file has no `Assistant` section today):

```json
  "Assistant": {
    "Quick": {
      "Enabled": true
    }
  },
```

Off by default everywhere else, so CI's `codegen` and `contract` jobs, which boot the API with no model, never try to load it.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Infrastructure.Tests --filter QuickCatalogLoaderTests --nologo` and `dotnet test tests/Worker.IntegrationTests --filter QuickIndexNotInWorkerTests --nologo`
Expected: 2 and 2 passed. (The worker test needs Docker, like every worker test.)

- [ ] **Step 6: Commit**

```bash
git add src/Infrastructure/Assistant/Quick src/Api/Program.cs src/Api/appsettings.json src/Api/appsettings.Development.json tests/Infrastructure.Tests/Assistant/Quick tests/Worker.IntegrationTests/QuickIndexNotInWorkerTests.cs
git commit -m "feat(assistant): build the instant-answer index when the API starts

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: The quick endpoint, its rate limit, and the status

**Files:**
- Modify: `src/Api/Assistant/AssistantController.cs`, `src/Api/Assistant/AssistantDtos.cs`, `src/Api/Assistant/AssistantRateLimiting.cs`, `src/Api/Assistant/AssistantConversationsController.cs`, `src/Api/Program.cs` (rate policy), `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` (regenerated)
- Test: `tests/Api.IntegrationTests/Assistant/AssistantQuickEndpointTests.cs`; existing `AssistantEndpointTests.cs` (status shape)

**Interfaces:**
- Consumes: `AskQuick`, `AssistantAvailability`, `AssistantCards`, `SseAssistantOutput`.
- Produces: `GET /api/assistant` → `{ quick: bool, ai: bool }`; `POST /api/assistant/quick` (body `AskAssistantRequest`, `text/event-stream`: `answer` or `suggestions`, then `done`; or `error`); `AssistantRateLimiting.QuickPolicyName = "assistant-quick"` (`RateLimiting:AssistantQuick:PermitLimit`, default 120 per user per hour); `AssistantMessageResponse.Answer`/`.Suggestions`.

- [ ] **Step 1: Write the failing integration tests**

Create `tests/Api.IntegrationTests/Assistant/AssistantQuickEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiFramework.Domain.Users;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Assistant;

[Collection(nameof(ApiFactoryCollection))]
public sealed class AssistantQuickEndpointTests(ApiFactory factory) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _quick = null!;

    public Task InitializeAsync()
    {
        // Instant answers on, the AI off: nothing here may reach a model.
        _quick = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Assistant:Quick:Enabled", "true");
            b.UseSetting("RateLimiting:AssistantQuick:PermitLimit", "1000");
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _quick.DisposeAsync();

    private async Task<HttpClient> MemberAsync(WebApplicationFactory<Program>? host = null)
    {
        var client = (host ?? _quick).CreateClient();
        (await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Username = $"m{Guid.NewGuid():N}"[..32], Password = ApiFactory.RegisteredPassword, DisplayName = "Member" })).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<List<JsonElement>> EventsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return [.. body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..])
            .Select(json => JsonDocument.Parse(json).RootElement.Clone())];
    }

    [Fact]
    public async Task Status_ReportsEachHalf()
    {
        var client = await MemberAsync();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/assistant");

        status.GetProperty("quick").GetBoolean().Should().BeTrue();
        status.GetProperty("ai").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Quick_AnswersAMembersOrderQuestionInstantly_AndSavesIt()
    {
        var client = await MemberAsync();

        var response = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Has my last order shipped?" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await EventsAsync(response);
        events.Select(e => e.GetProperty("type").GetString()).Should().Equal("answer", "done");
        var answer = events[0].GetProperty("answer");
        answer.GetProperty("kind").GetString().Should().Be("Instant");
        answer.GetProperty("text").GetString().Should().Be("You haven't placed any orders yet.");
        answer.GetProperty("link").GetProperty("to").GetString().Should().Be("/orders/new");

        var id = events[1].GetProperty("conversationId").GetString();
        var saved = await client.GetFromJsonAsync<JsonElement>($"/api/assistant/conversations/{id}");
        saved.GetProperty("messages")[1].GetProperty("answer").GetProperty("text").GetString()
            .Should().Be("You haven't placed any orders yet.");
    }

    [Fact]
    public async Task Quick_OffersSuggestions_ForAQuestionItCannotPlace_WithoutTheAi()
    {
        var client = await MemberAsync();

        var response = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Write me a poem about the rain" });

        var events = await EventsAsync(response);
        events[0].GetProperty("type").GetString().Should().Be("suggestions");
        events[0].GetProperty("suggestions").GetProperty("aiAvailable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Quick_NeverGivesAMemberMonitoringData()
    {
        var client = await MemberAsync();

        var response = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Did any background job fail today?" });

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("/monitoring");
        body.Should().NotContain("background job failed");
    }

    [Fact]
    public async Task Quick_GivesAnOperatorJobHealth()
    {
        var client = _quick.CreateClient();
        var username = $"a{Guid.NewGuid():N}"[..32];
        (await client.PostAsJsonAsync("/api/auth/register", new { Username = username, Password = ApiFactory.RegisteredPassword, DisplayName = "Admin" })).EnsureSuccessStatusCode();
        await factory.SetRoleAsync(username, UserRole.Admin);

        var response = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Did any background job fail today?" });

        var events = await EventsAsync(response);
        events[0].GetProperty("answer").GetProperty("link").GetProperty("to").GetString().Should().Be("/monitoring/jobs");
    }

    [Fact]
    public async Task Quick_WhenSwitchedOff_IsNotFound()
    {
        var client = await MemberAsync(factory);

        var response = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Has my last order shipped?" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Quick_HasItsOwnRateLimit()
    {
        await using var limited = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Assistant:Quick:Enabled", "true");
            b.UseSetting("RateLimiting:AssistantQuick:PermitLimit", "1");
        });
        var client = await MemberAsync(limited);

        (await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Show my recent orders" })).EnsureSuccessStatusCode();
        var second = await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Show my recent orders" });

        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
```

In `tests/Api.IntegrationTests/Assistant/AssistantEndpointTests.cs`, update the status assertions from `enabled` to the new shape: the disabled host reports `quick: false, ai: false`; the AI host reports `ai: true`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Api.IntegrationTests --filter "AssistantQuickEndpointTests|AssistantEndpointTests" --nologo`
Expected: failures: the status has no `quick`, and `/api/assistant/quick` is a 404.

- [ ] **Step 3: The status, the endpoint, the policy**

In `src/Api/Assistant/AssistantDtos.cs`, replace `AssistantStatusResponse`:

```csharp
/// <summary>Which halves of the assistant are on: instant answers, and the AI.</summary>
public sealed record AssistantStatusResponse
{
    public required bool Quick { get; init; }

    public required bool Ai { get; init; }
}
```

and add to `AssistantMessageResponse`:

```csharp
    /// <summary>A quick answer's card, when this message is one.</summary>
    public AssistantAnswerResponse? Answer { get; init; }

    /// <summary>The suggestions, when this message is a not-sure reply.</summary>
    public AssistantSuggestionsResponse? Suggestions { get; init; }
```

In `src/Api/Assistant/AssistantRateLimiting.cs`:

```csharp
/// <summary>The rate-limiter policies guarding the assistant, per user. Named once.</summary>
public static class AssistantRateLimiting
{
    /// <summary>The AI: a spend cap as much as a volume one (ADR 0033).</summary>
    public const string PolicyName = "assistant";

    /// <summary>Instant answers: no model is called, so this only bounds abuse (ADR 0036).</summary>
    public const string QuickPolicyName = "assistant-quick";
}
```

In `src/Api/Program.cs`, inside `AddRateLimiter`, after the assistant policy:

```csharp
    // Instant answers call no model, so this is a volume bound only, and generous. A separate
    // endpoint and policy because the limiter runs before model binding and cannot read the body.
    var quickPermitLimit = builder.Configuration.GetValue("RateLimiting:AssistantQuick:PermitLimit", defaultValue: 120);
    options.AddPolicy(AssistantRateLimiting.QuickPolicyName, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.RequestServices.GetRequiredService<ICurrentUser>().Id?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = quickPermitLimit,
                Window = TimeSpan.FromHours(1),
            }));
```

In `src/Api/Assistant/AssistantController.cs` (add `using AiFramework.Application.Assistant.Quick;`):

Replace `Status`:

```csharp
    /// <summary>Which halves of the assistant are on, so the UI shows only what works.</summary>
    [HttpGet]
    [ProducesResponseType<AssistantStatusResponse>(StatusCodes.Status200OK)]
    public ActionResult<AssistantStatusResponse> Status() =>
        new AssistantStatusResponse { Quick = availability.QuickEnabled, Ai = availability.AiEnabled };
```

In `Ask`, keep the `!availability.AiEnabled` 404, and replace the inline scope evaluation with `var scope = await ScopeAsync().ConfigureAwait(false);`. Add:

```csharp
    /// <summary>
    /// Asks one question of the instant path (ADR 0036). text/event-stream: answer or suggestions,
    /// then done; or error. No model is called. A failure before the first event is a problem.
    /// </summary>
    [HttpPost("quick")]
    [EnableRateLimiting(AssistantRateLimiting.QuickPolicyName)]
    [ProducesResponseType<AssistantEventResponse>(StatusCodes.Status200OK, "text/event-stream")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Quick(AskAssistantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!availability.QuickEnabled)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "assistant.quick_disabled", detail: "Instant answers are switched off.");
        }

        var scope = await ScopeAsync().ConfigureAwait(false);
        var result = await commands
            .SendAsync(new AskQuick(request.ConversationId, request.Question, scope), cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess && !output.Started)
        {
            return result.Problem(HttpContext);
        }

        await output.WriteAsync(
            result.IsSuccess
                ? AssistantEventResponse.Done(result.Value)
                : AssistantEventResponse.Error(result.Error.ToProblemDetails(HttpContext)),
            cancellationToken).ConfigureAwait(false);

        return new EmptyResult();
    }

    // The same policy the Monitoring pages use, evaluated per question: it reads the role from the
    // database, so a demoted administrator loses the monitoring answers on this very question.
    private async Task<ToolScope> ScopeAsync() =>
        (await authorization.AuthorizeAsync(User, AuthorizationPolicies.Monitoring.Read).ConfigureAwait(false)).Succeeded
            ? ToolScope.Operator
            : ToolScope.Member;
```

In `src/Api/Assistant/AssistantConversationsController.cs`, the message mapping is a `static` lambda; make it non-static and add the two properties (the controller already has `availability`):

```csharp
                Messages = [.. result.Value.Messages.Select(m => new AssistantMessageResponse
                {
                    Role = m.Role,
                    Text = m.Text,
                    Tools = m.Tools,
                    Answer = m.Answer is { } answer ? AssistantCards.ToResponse(answer) : null,
                    Suggestions = m.Suggestions is { } suggestions ? AssistantCards.ToResponse(suggestions, availability.AiEnabled) : null,
                })],
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Api.IntegrationTests --filter "Assistant" --nologo`
Expected: all passed. If `Quick_OffersSuggestions_ForAQuestionItCannotPlace_WithoutTheAi` gets an `answer`, the router is too eager: that question is also in the evaluation set as `unsure` (Task 13), whose tuning fixes it — do Task 13's tuning first, then come back.

- [ ] **Step 5: Regenerate the contract**

Follow the `regenerate` skill's contract commands exactly (bash):

```bash
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false Admin__ReconcileOnStart=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
git diff --stat -- openapi/ frontend/src/api/schema.d.ts
```

Expected: both files change: `AssistantStatusResponse` has `quick` and `ai`; new `/api/assistant/quick`; `AssistantAnswerResponse`, `AssistantSuggestionsResponse` and their parts; `QuickAnswerKind`. The Wolverine adapters do not change (no handler signature changed); confirm with the `regenerate` skill's codegen diff.

- [ ] **Step 6: Commit**

```bash
git add src/Api openapi frontend/src/api/schema.d.ts tests/Api.IntegrationTests/Assistant
git commit -m "feat(assistant): serve instant answers from their own rate-limited endpoint

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

(The frontend does not compile against the new `schema.d.ts` until Task 12; commit anyway, Task 12 follows directly.)

---

### Task 12: The chat shows cards, chips and Ask the AI

**Files:**
- Create: `frontend/src/features/assistant/quickLinks.ts`, `frontend/src/features/assistant/QuickAnswerCard.tsx`, `frontend/src/features/assistant/SuggestionsReply.tsx`, tests `quickLinks.test.ts`, `QuickAnswerCard.test.tsx`
- Modify: `frontend/src/features/assistant/types.ts`, `frontend/src/api/assistant.ts`, `frontend/src/features/assistant/conversation.ts`, `conversation.test.ts`, `useAssistantConversation.ts`, `Chat.tsx`, `Chat.test.tsx`, `AssistantPage.tsx`, `AssistantLink.tsx`, `assistant.css`, `frontend/src/test/handlers.ts`, and every test that stubs `/api/assistant` with `{ enabled: … }`

**Interfaces:**
- Consumes: the regenerated schema (Task 11).
- Produces: `AssistantAnswer`, `AssistantSuggestions`, `AskMode = 'quick' | 'ai'`; `askQuick(body, options)`; `isAllowedLink(to): boolean`; `AssistantMessage.answer|suggestions|source`; `ask(question, mode?)`; `<QuickAnswerCard answer />` (an `article` named "Instant answer" or "Help answer"); `<SuggestionsReply … />` (a button per option and "Ask the AI instead").

- [ ] **Step 1: Write the failing tests**

Create `frontend/src/features/assistant/quickLinks.test.ts`:

```ts
import { isAllowedLink } from './quickLinks';

describe('isAllowedLink', () => {
  it.each([
    '/orders',
    '/orders/new',
    '/orders/exports',
    '/orders/3f2a1b9c-0000-4000-8000-000000000001',
    '/products/3f2a1b9c-0000-4000-8000-000000000001',
    '/statistics/population',
    '/monitoring/jobs',
    '/account/password',
  ])('allows %s', (to) => {
    expect(isAllowedLink(to)).toBe(true);
  });

  it.each(['https://evil.example', '//evil.example', '/orders/../admin', 'javascript:alert(1)', '/orders?x=1', ''])(
    'refuses %s',
    (to) => {
      expect(isAllowedLink(to)).toBe(false);
    },
  );
});
```

Create `frontend/src/features/assistant/QuickAnswerCard.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QuickAnswerCard } from './QuickAnswerCard';
import type { AssistantAnswer } from './types';

const answer: AssistantAnswer = {
  kind: 'Instant',
  text: 'Your last order shipped on 3 Oct 2026, 14:12 UTC.',
  facts: [{ label: 'Product', value: 'Widget Pro (SKU-1042)' }],
  table: { columns: ['Order', 'Status'], rows: [['3f2a1b9c', 'Shipped']] },
  link: { label: 'Open order', to: '/orders/3f2a1b9c-0000-4000-8000-000000000001' },
};

function renderCard(card: AssistantAnswer): void {
  render(
    <MemoryRouter>
      <QuickAnswerCard answer={card} />
    </MemoryRouter>,
  );
}

describe('QuickAnswerCard', () => {
  it('shows the badge, the sentence, the facts, the table and the link', () => {
    renderCard(answer);

    const card = screen.getByRole('article', { name: 'Instant answer' });
    expect(card).toHaveTextContent('Instant answer');
    expect(card).toHaveTextContent('Your last order shipped on 3 Oct 2026, 14:12 UTC.');
    expect(screen.getByText('Widget Pro (SKU-1042)')).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: 'Shipped' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Open order/ })).toHaveAttribute(
      'href',
      '/orders/3f2a1b9c-0000-4000-8000-000000000001',
    );
  });

  it('names a help answer by its article', () => {
    renderCard({ ...answer, kind: 'Help', title: 'Exporting your orders', facts: [], table: null });

    expect(screen.getByRole('article', { name: 'Help answer' })).toHaveTextContent('Help · Exporting your orders');
  });

  it('says what it answered when the spelling was corrected', () => {
    renderCard({ ...answer, answeredAs: 'has my last order shipped' });

    expect(screen.getByText('Answered as: has my last order shipped')).toBeInTheDocument();
  });

  it('never renders a link outside the app', () => {
    renderCard({ ...answer, link: { label: 'Click me', to: 'https://evil.example' } });

    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('renders text, never markup', () => {
    renderCard({ ...answer, text: '<img src=x onerror=alert(1)>' });

    expect(screen.getByText('<img src=x onerror=alert(1)>')).toBeInTheDocument();
    expect(document.querySelector('img')).toBeNull();
  });
});
```

Add to `frontend/src/features/assistant/conversation.test.ts`:

```ts
describe('quick replies', () => {
  const asked = conversationReducer(initialConversation, { type: 'asked', question: 'Has it shipped?', mode: 'quick' });

  it('marks the answer as coming from the quick path', () => {
    expect(asked.messages.at(-1)?.source).toBe('quick');
  });

  it('puts an answer event on the answer', () => {
    const state = conversationReducer(asked, {
      type: 'event',
      event: { type: 'answer', answer: { kind: 'Instant', text: 'Shipped.', facts: [] } },
    });

    expect(state.messages.at(-1)?.answer?.text).toBe('Shipped.');
  });

  it('puts a suggestions event on the answer', () => {
    const state = conversationReducer(asked, {
      type: 'event',
      event: { type: 'suggestions', suggestions: { text: 'Not sure.', options: ['Is my export ready?'], aiAvailable: true } },
    });

    expect(state.messages.at(-1)?.suggestions?.options).toEqual(['Is my export ready?']);
  });

  it('keeps an answer that has a card but no text when the stream ends', () => {
    const answered = conversationReducer(asked, {
      type: 'event',
      event: { type: 'answer', answer: { kind: 'Instant', text: 'Shipped.', facts: [] } },
    });
    const state = conversationReducer(answered, { type: 'event', event: { type: 'done', conversationId: 'c1' } });

    expect(state.messages).toHaveLength(2);
  });

  it('restores a saved quick answer with its card', () => {
    const state = fromSaved({
      id: 'c1',
      title: 'Has it shipped?',
      messages: [
        { role: 'user', text: 'Has it shipped?', tools: [] },
        { role: 'assistant', text: 'Shipped.', tools: [], answer: { kind: 'Instant', text: 'Shipped.', facts: [] } },
      ],
    });

    expect(state.messages[1]?.answer?.text).toBe('Shipped.');
    expect(state.messages[1]?.source).toBe('quick');
  });
});
```

(Every existing `{ type: 'asked', question }` in this file gains `mode: 'ai'`.)

Add to `frontend/src/features/assistant/Chat.test.tsx` (reuse the file's existing render helper and imports; add `QuickAnswer`-related handlers):

```tsx
describe('instant answers', () => {
  it('asks the quick endpoint when instant answers are on, and shows the card', async () => {
    server.use(
      http.post('/api/assistant/quick', () =>
        sseResponse(
          sseEvent({ type: 'answer', answer: { kind: 'Instant', text: 'You have no orders yet.', facts: [], link: { label: 'Place an order', to: '/orders/new' } } }),
          sseEvent({ type: 'done', conversationId: 'c1', outcome: 'Answered' }),
        ),
      ),
    );
    renderChat({ quickEnabled: true, aiEnabled: false });

    await userEvent.type(screen.getByRole('textbox', { name: 'Your question' }), 'Has my last order shipped?');
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByRole('article', { name: 'Instant answer' })).toHaveTextContent('You have no orders yet.');
  });

  it('asks a suggestion instantly, and the AI only when asked', async () => {
    const quick: string[] = [];
    const ai: string[] = [];
    server.use(
      http.post('/api/assistant/quick', async ({ request }) => {
        quick.push(((await request.json()) as { question: string }).question);
        return sseResponse(
          sseEvent({ type: 'suggestions', suggestions: { text: "I'm not sure what you mean.", options: ['Is my export ready?'], aiAvailable: true } }),
          sseEvent({ type: 'done', conversationId: 'c1', outcome: 'Answered' }),
        );
      }),
      http.post('/api/assistant/messages', async ({ request }) => {
        ai.push(((await request.json()) as { question: string }).question);
        return sseResponse(sseEvent({ type: 'text', delta: 'A poem.' }), sseEvent({ type: 'done', conversationId: 'c1', outcome: 'Answered' }));
      }),
    );
    renderChat({ quickEnabled: true, aiEnabled: true });

    await userEvent.type(screen.getByRole('textbox', { name: 'Your question' }), 'Write me a poem');
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Is my export ready?' }));
    await userEvent.click((await screen.findAllByRole('button', { name: /Ask the AI/ }))[0] as HTMLElement);

    expect(quick).toEqual(['Write me a poem', 'Is my export ready?']);
    await waitFor(() => {
      expect(ai).toEqual(['Write me a poem']);
    });
  });

  it('hides Ask the AI when the AI is off', async () => {
    server.use(
      http.post('/api/assistant/quick', () =>
        sseResponse(
          sseEvent({ type: 'suggestions', suggestions: { text: "I'm not sure what you mean.", options: [], aiAvailable: false } }),
          sseEvent({ type: 'done', conversationId: 'c1', outcome: 'Answered' }),
        ),
      ),
    );
    renderChat({ quickEnabled: true, aiEnabled: false });

    await userEvent.type(screen.getByRole('textbox', { name: 'Your question' }), 'Write me a poem');
    await userEvent.click(screen.getByRole('button', { name: 'Send' }));

    expect(await screen.findByText("I'm not sure what you mean.")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Ask the AI/ })).not.toBeInTheDocument();
  });
});
```

(Read `Chat.test.tsx` first: if its render helper does not take props, give it an optional `Partial<ChatProps>` that is spread onto `<Chat …>`; `renderChat` above assumes that. `findAllByRole(...)[0] as HTMLElement` is there because `noUncheckedIndexedAccess` types it `HTMLElement | undefined`; if the repo has a helper for that, use it.)

- [ ] **Step 2: Run them to verify they fail**

Run (from `frontend/`): `npx vitest run src/features/assistant`
Expected: failures: `quickLinks` and `QuickAnswerCard` not found, `mode` unknown on `asked`, no quick endpoint call.

- [ ] **Step 3: Types and the client**

In `frontend/src/features/assistant/types.ts`, add:

```ts
/** An instant or help answer: already formatted, rendered as a card, never as Markdown. */
export type AssistantAnswer = components['schemas']['AssistantAnswerResponse'];

export type AssistantSuggestions = components['schemas']['AssistantSuggestionsResponse'];

/** Which path answers: the local router (quick) or the AI. */
export type AskMode = 'quick' | 'ai';
```

In `frontend/src/api/assistant.ts`, rename the body of `askAssistant` into a private `streamAnswer(path, body, options)` (unchanged apart from taking `path`), and export:

```ts
/** The AI path (ADR 0033): text and tool events, then done or error. */
export function askAssistant(body: AskAssistantRequest, options: AskOptions): Promise<void> {
  return streamAnswer('/api/assistant/messages', body, options);
}

/**
 * The instant path (ADR 0036): one answer or suggestions event, then done; or error. Same failure
 * contract as askAssistant.
 */
export function askQuick(body: AskAssistantRequest, options: AskOptions): Promise<void> {
  return streamAnswer('/api/assistant/quick', body, options);
}
```

Create `frontend/src/features/assistant/quickLinks.ts`:

```ts
/**
 * The screens an answer may link to. A link the server sends is rendered only if it matches one of
 * these exactly, so a reply can never send someone off the app. Kept in step with
 * tests/Application.Tests/Assistant/Quick/QuickCatalogTests.cs.
 */
const allowed: readonly RegExp[] = [
  /^\/orders$/,
  /^\/orders\/new$/,
  /^\/orders\/exports$/,
  /^\/orders\/[0-9a-f-]{36}$/,
  /^\/products$/,
  /^\/products\/[0-9a-f-]{36}$/,
  /^\/statistics\/population$/,
  /^\/monitoring(\/(jobs|traffic|integrations|logins|users))?$/,
  /^\/account\/password$/,
  /^\/notifications$/,
];

export function isAllowedLink(to: string): boolean {
  return allowed.some((pattern) => pattern.test(to));
}
```

- [ ] **Step 4: The reducer and the hook**

In `frontend/src/features/assistant/conversation.ts`:

```ts
import type { AskMode, AssistantAnswer, AssistantEvent, AssistantOutcome, AssistantSuggestions, SavedConversation } from './types';

export interface AssistantMessage {
  readonly id: number;
  readonly role: 'user' | 'assistant';
  readonly text: string;
  readonly tools: readonly string[];
  readonly outcome?: AssistantOutcome | undefined;
  readonly stopped?: boolean | undefined;
  /** An instant or help answer's card. */
  readonly answer?: AssistantAnswer | undefined;
  /** A not-sure reply's suggestions. */
  readonly suggestions?: AssistantSuggestions | undefined;
  /** Which path wrote this answer. */
  readonly source?: AskMode | undefined;
}
```

`ConversationAction`'s `asked` becomes `{ readonly type: 'asked'; readonly question: string; readonly mode: AskMode }`, and its reducer case adds `source: action.mode` to the new assistant message. In `applyEvent`, before `default`:

```ts
    case 'answer':
      return updateAnswer(state, (m) => ({ ...m, answer: event.answer ?? undefined }));
    case 'suggestions':
      return updateAnswer(state, (m) => ({ ...m, suggestions: event.suggestions ?? undefined }));
```

`dropEmptyAnswer`'s condition also requires `last.answer === undefined && last.suggestions === undefined`. In `fromSaved`, each message adds:

```ts
      answer: message.answer ?? undefined,
      suggestions: message.suggestions ?? undefined,
      source: message.answer || message.suggestions ? 'quick' : 'ai',
```

In `frontend/src/features/assistant/useAssistantConversation.ts`: import `askQuick` and `AskMode`; `AssistantConversation.ask` becomes `(question: string, mode?: AskMode) => Promise<AskOutcome>`; inside `ask`, take `mode: AskMode = 'ai'`, dispatch `{ type: 'asked', question, mode }`, and call `(mode === 'quick' ? askQuick : askAssistant)(…)` with the same arguments as today.

- [ ] **Step 5: The components**

Create `frontend/src/features/assistant/QuickAnswerCard.tsx`:

```tsx
import { Link } from 'react-router-dom';
import { isAllowedLink } from './quickLinks';
import type { AssistantAnswer } from './types';

interface QuickAnswerCardProps {
  readonly answer: AssistantAnswer;
}

/**
 * An instant or help answer, from structured data only: every string is rendered as text, and the
 * link only if it is one of the app's own screens. Never Markdown, never HTML. ADR 0036.
 */
export function QuickAnswerCard({ answer }: QuickAnswerCardProps): React.JSX.Element {
  const help = answer.kind === 'Help';

  return (
    <article className="quick-answer" aria-label={help ? 'Help answer' : 'Instant answer'}>
      <p className="assistant-badge">{help ? `Help · ${answer.title ?? ''}` : 'Instant answer'}</p>
      <p className="quick-answer__text">{answer.text}</p>
      {answer.answeredAs && <p className="muted">Answered as: {answer.answeredAs}</p>}
      {answer.facts.length > 0 && (
        <dl className="quick-answer__facts">
          {answer.facts.map((fact) => (
            <div key={fact.label}>
              <dt>{fact.label}</dt>
              <dd>{fact.value}</dd>
            </div>
          ))}
        </dl>
      )}
      {answer.table && (
        <table className="quick-answer__table">
          <thead>
            <tr>
              {answer.table.columns.map((column) => (
                <th key={column} scope="col">
                  {column}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {answer.table.rows.map((row, index) => (
              // Rows never reorder: the answer is fixed once written.
              <tr key={String(index)}>
                {row.map((cell, column) => (
                  <td key={String(column)}>{cell}</td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {answer.link && isAllowedLink(answer.link.to) && (
        <Link className="quick-answer__link" to={answer.link.to}>
          {answer.link.label} →
        </Link>
      )}
    </article>
  );
}
```

Create `frontend/src/features/assistant/SuggestionsReply.tsx`:

```tsx
import type { AssistantSuggestions } from './types';

interface SuggestionsReplyProps {
  readonly suggestions: AssistantSuggestions;
  /** Asks a suggestion on the instant path: it is an intent's own question, so it is answered at once. */
  readonly onPick: (question: string) => void;
  /** Asks the original question of the AI. Absent when the AI is off. */
  readonly onAskAi?: (() => void) | undefined;
  readonly busy: boolean;
}

export function SuggestionsReply({ suggestions, onPick, onAskAi, busy }: SuggestionsReplyProps): React.JSX.Element {
  return (
    <div className="quick-suggestions">
      <p className="assistant-badge">Not sure what you mean</p>
      <p>{suggestions.text}</p>
      {suggestions.options.length > 0 && (
        <div className="quick-suggestions__options">
          {suggestions.options.map((option) => (
            <button key={option} type="button" disabled={busy} onClick={() => { onPick(option); }}>
              {option}
            </button>
          ))}
        </div>
      )}
      {onAskAi && (
        <button className="quick-suggestions__ai" type="button" disabled={busy} onClick={onAskAi}>
          Ask the AI instead (takes a few seconds)
        </button>
      )}
    </div>
  );
}
```

In `frontend/src/features/assistant/Chat.tsx`:

- `ChatProps` gains `readonly quickEnabled?: boolean | undefined; readonly aiEnabled?: boolean | undefined;`, defaulted in the signature to `quickEnabled = false, aiEnabled = true` (so existing callers and tests keep today's AI behaviour).
- `send()` calls `conversation.ask(question, quickEnabled ? 'quick' : 'ai')`.
- The log passes each `Message` the question before it and two callbacks:

```tsx
              {conversation.messages.map((message, index) => {
                const previous = conversation.messages[index - 1];
                return (
                  <Message
                    key={message.id}
                    message={message}
                    thinking={conversation.streaming && index === conversation.messages.length - 1 && message.text === '' && !message.answer && !message.suggestions}
                    busy={conversation.streaming}
                    onPick={(question) => void conversation.ask(question, 'quick')}
                    onAskAi={
                      aiEnabled && message.suggestions?.aiAvailable && previous?.role === 'user'
                        ? () => void conversation.ask(previous.text, 'ai')
                        : undefined
                    }
                  />
                );
              })}
```

- `MessageProps` gains `busy: boolean`, `onPick: (question: string) => void`, `onAskAi?: (() => void) | undefined`. In `Message`, after the user branch:

```tsx
  if (message.answer) {
    return (
      <li className="assistant-message assistant-message--assistant">
        <span className="visually-hidden">Assistant: </span>
        <QuickAnswerCard answer={message.answer} />
      </li>
    );
  }

  if (message.suggestions) {
    return (
      <li className="assistant-message assistant-message--assistant">
        <span className="visually-hidden">Assistant: </span>
        <SuggestionsReply suggestions={message.suggestions} onPick={onPick} onAskAi={onAskAi} busy={busy} />
      </li>
    );
  }
```

and in the AI branch, before the tools list: `{message.source === 'ai' && message.text !== '' && <p className="assistant-badge assistant-badge--ai">AI answer</p>}`.

In `frontend/src/features/assistant/AssistantPage.tsx`: the "switched off" check becomes `if (!status.data.quick && !status.data.ai)`; pass `quickEnabled={status.data.quick} aiEnabled={status.data.ai}` to `<Chat …>` and to `<SavedChat …>` (add both to `SavedChatProps`, which spreads them on to `Chat`).

In `frontend/src/features/assistant/AssistantLink.tsx`: `if (status.data?.quick !== true && status.data?.ai !== true) { return null; }`, and update its doc comment ("Hidden while both halves are off").

In `frontend/src/features/assistant/assistant.css`, add (tokens only):

```css
.assistant-badge {
  display: inline-block;
  margin: 0 0 var(--space-2);
  padding: 0 var(--space-2);
  border-radius: var(--radius-full);
  background-color: var(--color-success-soft);
  color: var(--color-success-text);
  font-size: var(--text-xs);
  font-weight: 600;
}

.assistant-badge--ai {
  background-color: var(--color-accent-soft);
  color: var(--color-accent);
}

.quick-answer,
.quick-suggestions {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}

.quick-answer__facts {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: var(--space-1) var(--space-4);
  margin: 0;
  padding: var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  background-color: var(--color-surface-sunken);
}

.quick-answer__facts > div {
  display: contents;
}

.quick-answer__facts dt {
  color: var(--color-text-muted);
}

.quick-answer__facts dd {
  margin: 0;
  font-weight: 600;
}

.quick-answer__table {
  width: 100%;
  border-collapse: collapse;
  font-size: var(--text-sm);
}

.quick-answer__table th,
.quick-answer__table td {
  padding: var(--space-1) var(--space-2);
  border-block-end: 1px solid var(--color-border);
  text-align: start;
}

.quick-answer__link,
.quick-suggestions__ai {
  align-self: flex-start;
}

.quick-suggestions__options {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
}
```

- [ ] **Step 6: The status stubs**

In `frontend/src/test/handlers.ts`, the default becomes `http.get('/api/assistant', () => HttpResponse.json({ quick: false, ai: false }))`. Then run `rg -n "enabled: (true|false)" src` and change each assistant-status stub: `{ enabled: true }` → `{ quick: false, ai: true }`, `{ enabled: false }` → `{ quick: false, ai: false }`.

- [ ] **Step 7: Run everything to verify it passes**

Run (from `frontend/`): `npx vitest run`, `npx tsc -b`, `npx eslint . --max-warnings 0`
Expected: all green.

- [ ] **Step 8: Commit**

```bash
git add frontend/src
git commit -m "feat(assistant): show instant answers, suggestions and Ask the AI in the chat

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: The evaluation gate, and setting the thresholds

**Files:**
- Create: `tests/Infrastructure.Tests/Assistant/Quick/evaluation.en.json`, `tests/Infrastructure.Tests/Assistant/Quick/QuickEvaluationTests.cs`
- Modify: `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj` (copy the JSON), `src/Application/Assistant/Quick/IntentRouter.cs` (`QuickRouting.Default`), `src/Application/Assistant/Quick/Catalog/en/phrases.json` (only if tuning needs phrases)

**Interfaces:**
- Consumes: the real `OnnxTextEmbedder`, `QuickCatalogFiles.Load`, `QuickIndexBuilder`, `IntentRouter`.
- Produces: a CI gate (zero wrong instant answers) and measured floors; tuned `QuickRouting.Default`.

- [ ] **Step 1: Write the evaluation set**

Rules for this file: **never copy one of its questions into `phrases.json`** (that would test the catalog against itself); labels are an intent id, `help:<article id>`, or `unsure`; `typo: true` marks a question with deliberate misspellings.

Create `tests/Infrastructure.Tests/Assistant/Quick/evaluation.en.json`:

```json
[
  { "question": "has my newest order been shipped", "expect": "order_status", "scope": "member" },
  { "question": "where's my most recent order now", "expect": "order_status", "scope": "member" },
  { "question": "is my order shipped already?", "expect": "order_status", "scope": "member" },
  { "question": "what's going on with my last order", "expect": "order_status", "scope": "member" },
  { "question": "has the order I placed shipped", "expect": "order_status", "scope": "member" },
  { "question": "did my order go out yet", "expect": "order_status", "scope": "member" },
  { "question": "is my last order on the way", "expect": "order_status", "scope": "member" },
  { "question": "check whether my order has shipped", "expect": "order_status", "scope": "member" },
  { "question": "what is the state of my latest order", "expect": "order_status", "scope": "member" },
  { "question": "show me everything I've ordered", "expect": "recent_orders", "scope": "member" },
  { "question": "list all of my orders", "expect": "recent_orders", "scope": "member" },
  { "question": "what orders have I made", "expect": "recent_orders", "scope": "member" },
  { "question": "my order history please", "expect": "recent_orders", "scope": "member" },
  { "question": "show me my previous orders", "expect": "recent_orders", "scope": "member" },
  { "question": "overview of my recent orders", "expect": "recent_orders", "scope": "member" },
  { "question": "is the pdf of my orders ready", "expect": "export_status", "scope": "member" },
  { "question": "has my export completed", "expect": "export_status", "scope": "member" },
  { "question": "can I download the export now", "expect": "export_status", "scope": "member" },
  { "question": "is my orders export finished yet", "expect": "export_status", "scope": "member" },
  { "question": "check my export status", "expect": "export_status", "scope": "member" },
  { "question": "how much does the widget cost", "expect": "product_info", "scope": "member" },
  { "question": "what's the price of the gadget", "expect": "product_info", "scope": "member" },
  { "question": "how expensive is the sprocket", "expect": "product_info", "scope": "member" },
  { "question": "price for widget pro please", "expect": "product_info", "scope": "member" },
  { "question": "what does the sprocket cost", "expect": "product_info", "scope": "member" },
  { "question": "how many people live in copenhagen", "expect": "population", "scope": "member" },
  { "question": "population of aarhus please", "expect": "population", "scope": "member" },
  { "question": "what's the population of odense", "expect": "population", "scope": "member" },
  { "question": "how many residents does denmark have", "expect": "population", "scope": "member" },
  { "question": "number of inhabitants in aalborg", "expect": "population", "scope": "member" },
  { "question": "how can I order something", "expect": "help:place-order", "scope": "member" },
  { "question": "how do I buy a product", "expect": "help:place-order", "scope": "member" },
  { "question": "where do I place an order", "expect": "help:place-order", "scope": "member" },
  { "question": "how do I get my orders as a pdf", "expect": "help:export-orders", "scope": "member" },
  { "question": "how can I export my orders", "expect": "help:export-orders", "scope": "member" },
  { "question": "how can I change my password", "expect": "help:change-password", "scope": "member" },
  { "question": "I want to set a new password", "expect": "help:change-password", "scope": "member" },
  { "question": "how do I log out on all my devices", "expect": "help:sign-out-everywhere", "scope": "member" },
  { "question": "sign me out of every session", "expect": "help:sign-out-everywhere", "scope": "member" },
  { "question": "how do notifications work here", "expect": "help:notifications", "scope": "member" },
  { "question": "how do I mark notifications as read", "expect": "help:notifications", "scope": "member" },
  { "question": "where can I browse the products", "expect": "help:catalogue", "scope": "member" },
  { "question": "how do I see the catalogue", "expect": "help:catalogue", "scope": "member" },
  { "question": "has my last ordr shipped", "expect": "order_status", "scope": "member", "typo": true },
  { "question": "where is my lastest order", "expect": "order_status", "scope": "member", "typo": true },
  { "question": "is my exprot ready", "expect": "export_status", "scope": "member", "typo": true },
  { "question": "show my recnet orders", "expect": "recent_orders", "scope": "member", "typo": true },
  { "question": "how much is the widgte", "expect": "product_info", "scope": "member", "typo": true },
  { "question": "populaton of copenhagen", "expect": "population", "scope": "member", "typo": true },
  { "question": "how do I chnage my password", "expect": "help:change-password", "scope": "member", "typo": true },
  { "question": "were there job failures today", "expect": "job_health", "scope": "operator" },
  { "question": "how healthy are the background jobs", "expect": "job_health", "scope": "operator" },
  { "question": "did any jobs fail in the last hour", "expect": "job_health", "scope": "operator" },
  { "question": "job failures this week?", "expect": "job_health", "scope": "operator" },
  { "question": "list the job runs that failed", "expect": "failed_jobs", "scope": "operator" },
  { "question": "show me failed background jobs", "expect": "failed_jobs", "scope": "operator" },
  { "question": "which job runs errored", "expect": "failed_jobs", "scope": "operator" },
  { "question": "anything in the dead letter queue?", "expect": "dead_letters", "scope": "operator" },
  { "question": "show dead-lettered messages", "expect": "dead_letters", "scope": "operator" },
  { "question": "are any messages dead lettered", "expect": "dead_letters", "scope": "operator" },
  { "question": "what's the API error rate", "expect": "traffic", "scope": "operator" },
  { "question": "how fast is the API responding", "expect": "traffic", "scope": "operator" },
  { "question": "how many requests per minute are we serving", "expect": "traffic", "scope": "operator" },
  { "question": "are external integrations up", "expect": "integrations", "scope": "operator" },
  { "question": "is any partner system down", "expect": "integrations", "scope": "operator" },
  { "question": "status of external systems", "expect": "integrations", "scope": "operator" },
  { "question": "how many failed logins today", "expect": "sign_ins", "scope": "operator" },
  { "question": "are any users locked out", "expect": "sign_ins", "scope": "operator" },
  { "question": "how many sign-ins failed in the last 24 hours", "expect": "sign_ins", "scope": "operator" },
  { "question": "did any job fial today", "expect": "job_health", "scope": "operator", "typo": true },
  { "question": "show the dead leters", "expect": "dead_letters", "scope": "operator", "typo": true },
  { "question": "are the partner sytems up", "expect": "integrations", "scope": "operator", "typo": true },
  { "question": "has my last order shipped?", "expect": "order_status", "scope": "operator" },
  { "question": "Did any background job fail today?", "expect": "unsure", "scope": "member" },
  { "question": "Show the dead letters", "expect": "unsure", "scope": "member" },
  { "question": "What is the error rate?", "expect": "unsure", "scope": "member" },
  { "question": "Write me a poem about the rain", "expect": "unsure", "scope": "member" },
  { "question": "Write me a poem about the rain", "expect": "unsure", "scope": "operator" },
  { "question": "What is the capital of France?", "expect": "unsure", "scope": "member" },
  { "question": "Tell me a joke", "expect": "unsure", "scope": "member" },
  { "question": "Who won the football match yesterday?", "expect": "unsure", "scope": "member" },
  { "question": "What's the weather like?", "expect": "unsure", "scope": "member" },
  { "question": "Translate hello into German", "expect": "unsure", "scope": "member" },
  { "question": "Can you help me with my homework?", "expect": "unsure", "scope": "member" },
  { "question": "What is the meaning of life?", "expect": "unsure", "scope": "operator" },
  { "question": "Recommend a good book", "expect": "unsure", "scope": "member" },
  { "question": "How do I bake bread?", "expect": "unsure", "scope": "member" },
  { "question": "What's 17 times 23?", "expect": "unsure", "scope": "operator" }
]
```

Add to `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj`:

```xml
  <ItemGroup>
    <None Update="Assistant\Quick\evaluation.en.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: Write the gate**

Create `tests/Infrastructure.Tests/Assistant/Quick/QuickEvaluationTests.cs`:

```csharp
using System.Text.Json;
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Infrastructure.Assistant.Quick;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace AiFramework.Infrastructure.Tests.Assistant.Quick;

/// <summary>
/// The precision gate (ADR 0036): every labelled question through the real model, catalog and
/// router. A wrong instant answer fails CI; the instant rates are printed and held above a floor
/// measured when the thresholds were last set.
/// </summary>
public sealed class QuickEvaluationTests(ITestOutputHelper output) : IDisposable
{
    // Set from a measured run (Task 13, Step 5): measured rate minus 0.05, rounded down to two places.
    private const double InstantFloor = 0;
    private const double TypoFloor = 0;

    private readonly OnnxTextEmbedder _embedder = new(Options.Create(new QuickModelOptions()));

    public void Dispose() => _embedder.Dispose();

    private sealed record Case(string Question, string Expect, string Scope, bool Typo = false);

    private sealed record Outcome(List<string> Wrong, int InCatalog, int Instant, int Typos, int TypoInstant, int Unsure, int UnsureHeld);

    private Outcome Evaluate()
    {
        var built = QuickIndexBuilder.Build(QuickCatalogFiles.Load(), _embedder);
        built.IsSuccess.Should().BeTrue("the model must be fetched: run scripts/fetch-assistant-model.ps1");
        var state = new QuickIndexState();
        state.Publish(built.Value.Index, built.Value.Spelling);
        var router = new IntentRouter(state, _embedder, QuickRouting.Default);

        var cases = JsonSerializer.Deserialize<List<Case>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assistant", "Quick", "evaluation.en.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];

        var outcome = new Outcome([], 0, 0, 0, 0, 0, 0);
        foreach (var c in cases)
        {
            var scope = c.Scope == "operator" ? ToolScope.Operator : ToolScope.Member;
            var got = router.Route(c.Question, scope).Value switch
            {
                RouteToIntent intent => intent.Intent.Id,
                RouteToHelp help => $"help:{help.Section.ArticleId}",
                _ => "unsure",
            };

            if (c.Expect == "unsure")
            {
                outcome = outcome with { Unsure = outcome.Unsure + 1, UnsureHeld = outcome.UnsureHeld + (got == "unsure" ? 1 : 0) };
                if (got != "unsure")
                {
                    outcome.Wrong.Add($"[{c.Scope}] '{c.Question}': expected unsure, answered {got}");
                }

                continue;
            }

            outcome = outcome with { InCatalog = outcome.InCatalog + 1, Typos = outcome.Typos + (c.Typo ? 1 : 0) };
            if (got == "unsure")
            {
                continue;
            }

            if (got != c.Expect)
            {
                outcome.Wrong.Add($"[{c.Scope}] '{c.Question}': expected {c.Expect}, answered {got}");
                continue;
            }

            outcome = outcome with { Instant = outcome.Instant + 1, TypoInstant = outcome.TypoInstant + (c.Typo ? 1 : 0) };
        }

        output.WriteLine(
            $"Instant: {outcome.Instant}/{outcome.InCatalog} ({Rate(outcome.Instant, outcome.InCatalog):P0}); " +
            $"typos: {outcome.TypoInstant}/{outcome.Typos} ({Rate(outcome.TypoInstant, outcome.Typos):P0}); " +
            $"unsure held: {outcome.UnsureHeld}/{outcome.Unsure}. Routing: {QuickRouting.Default}");
        return outcome;
    }

    private static double Rate(int part, int whole) => whole == 0 ? 0 : (double)part / whole;

    [Fact]
    public void NoLabelledQuestion_IsAnsweredInstantlyWithTheWrongTarget()
    {
        var outcome = Evaluate();

        string.Join('\n', outcome.Wrong).Should().BeEmpty("a wrong instant answer is the failure that matters most");
    }

    [Fact]
    public void TheInstantRates_StayAboveTheMeasuredFloor()
    {
        var outcome = Evaluate();

        Rate(outcome.Instant, outcome.InCatalog).Should().BeGreaterThanOrEqualTo(InstantFloor);
        Rate(outcome.TypoInstant, outcome.Typos).Should().BeGreaterThanOrEqualTo(TypoFloor);
    }
}
```

- [ ] **Step 3: Run it and read the numbers**

Run: `dotnet test tests/Infrastructure.Tests --filter QuickEvaluationTests --nologo --logger "console;verbosity=detailed"`
Expected first time: `TheInstantRates_StayAboveTheMeasuredFloor` passes (floors are 0); `NoLabelledQuestion_…` may fail with a list of wrong answers. Read the `Instant: … typos: … unsure held: …` line.

- [ ] **Step 4: Tune until nothing is wrong**

In order, re-running Step 3 after each change:
1. Raise `Margin` in `QuickRouting.Default` in steps of 0.01 (up to 0.10) while any question is wrongly answered between two intents.
2. Raise `Threshold` in steps of 0.01 (up to 0.90) while an `unsure` question is answered.
3. Only if a wrong answer survives both: add example phrases for the intent it *should* have hit, worded differently from the evaluation question (never the same question), or remove a phrase that pulls unrelated questions towards the wrong intent.

Stop at the lowest `Threshold`/`Margin` with zero wrong answers: the instant rate falls as either rises. Then set `appsettings.json`'s `Assistant:Quick` to carry the same two numbers only if they differ from what the code defaults say (they should not: the defaults are the tuned values).

If tuning cannot reach zero wrong with `Threshold ≤ 0.90` and phrase changes, stop and report: that would mean this model is not precise enough, the one finding that reopens the spec's model choice.

- [ ] **Step 5: Set the floors**

From the last run's line, set `InstantFloor` and `TypoFloor` to the measured rate minus 0.05, rounded down to two decimal places (for example, 0.871 measured → 0.82). Replace the comment above them with the measured numbers and the date: `// Measured 2026-10-?? with Threshold … Margin …: instant 0.87, typos 0.86.`

Run Step 3 again. Expected: both tests pass.

- [ ] **Step 6: Commit**

```bash
git add tests/Infrastructure.Tests src/Application/Assistant/Quick
git commit -m "feat(assistant): gate instant answers on a labelled evaluation set

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: CI, the image, kind, and the e2e stack

**Files:**
- Modify: `.github/workflows/ci.yml` (backend job), `.github/workflows/e2e.yml`, `Dockerfile.api`, `.dockerignore`, `frontend/e2e/setup/prepare-database.ts`, `frontend/playwright.config.ts`, `k8s/base/api.yaml`, `k8s/overlays/local/config.yaml`

- [ ] **Step 1: CI fetches the model before building**

In `.github/workflows/ci.yml`, in the `backend` job, immediately before `- run: dotnet restore`, add (same `actions/cache@v6` the NuGet step uses):

```yaml
      # The assistant's embedding model (ADR 0036): the Api and Infrastructure.Tests projects copy
      # it beside their binaries at build time, and the embedder and evaluation tests fail without it.
      - name: Cache the assistant model
        uses: actions/cache@v6
        with:
          path: models/assistant
          key: assistant-model-${{ hashFiles('scripts/fetch-assistant-model.ps1') }}

      - name: Fetch the assistant model
        shell: pwsh
        run: ./scripts/fetch-assistant-model.ps1
```

In `.github/workflows/e2e.yml`, add the same two steps immediately before `- name: Build backend`.

The `codegen` and `contract` jobs need nothing: `Assistant:Quick:Enabled` is false outside Development, so those hosts never load the model.

- [ ] **Step 2: The image fetches it too**

In `Dockerfile.api`, after the two `dotnet restore` lines and before `COPY src/ src/`:

```dockerfile
# The assistant's embedding model (ADR 0036), fetched and SHA-256-checked here, before the publish,
# so the Api project's conditional item copies it beside the binaries as assistant-model/. Its own
# layer, cached until the script (and with it the pinned hash) changes. The SDK image has pwsh.
COPY scripts/fetch-assistant-model.ps1 scripts/
RUN pwsh -NoProfile -File scripts/fetch-assistant-model.ps1
```

In `.dockerignore`, add `models/` under the list of excluded paths, with the comment `# Fetched inside the image (Dockerfile.api); never sent as build context.`

- [ ] **Step 3: kind turns instant answers on, with room for the model**

In `k8s/overlays/local/config.yaml`, add `Assistant__Quick__Enabled: "true"` to the API's configuration, following the file's existing key style (read it first).

In `k8s/base/api.yaml`, raise the API container's `requests.memory` from `256Mi` to `320Mi` and add to the comment above `resources:` one line: `# The instant-answer model (ADR 0036) adds about 60 MB resident; the request covers it.` The `768Mi` limit stays.

- [ ] **Step 4: The e2e stack**

In `frontend/e2e/setup/prepare-database.ts`, directly after `execFileSync('dotnet', ['tool', 'restore'], { stdio: 'inherit' });`:

```ts
// The assistant's embedding model (ADR 0036): the API build below copies it beside its binaries,
// and instant answers are on in the stack Playwright starts.
execFileSync('pwsh', ['-NoProfile', '-File', '../scripts/fetch-assistant-model.ps1'], { stdio: 'inherit' });
```

In `frontend/playwright.config.ts`, in the API's environment next to the other `Assistant__*` keys:

```ts
              // Instant answers (ADR 0036), from the model the setup fetched.
              Assistant__Quick__Enabled: 'true',
              RateLimiting__AssistantQuick__PermitLimit: '1000000',
```

- [ ] **Step 5: Verify the image builds and carries the model**

Run: `docker build -f Dockerfile.api --target runtime -t aiframework-api:quick-check .`
Expected: success; the fetch step prints `download  model.onnx`. Then:
`docker create --name quick-check aiframework-api:quick-check && docker cp quick-check:/app/assistant-model ./.quick-check && ls ./.quick-check; docker rm quick-check; rm -rf ./.quick-check`
Expected: `model.onnx  vocab.txt`.

- [ ] **Step 6: Commit**

```bash
git add .github/workflows Dockerfile.api .dockerignore frontend/e2e/setup/prepare-database.ts frontend/playwright.config.ts k8s
git commit -m "feat(assistant): fetch the model in CI, the image and the e2e stack

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 15: End to end

**Files:**
- Create: `frontend/e2e/specs/assistant/quick-answers.spec.ts`
- Modify: `frontend/e2e/screens/assistant.ts`, `frontend/e2e/specs/assistant/assistant.spec.ts`, `frontend/e2e/CLAUDE.md`

- [ ] **Step 1: Screen locators**

Append to `frontend/e2e/screens/assistant.ts`:

```ts
/** The last instant or help answer: an article named "Instant answer" or "Help answer". */
export const answerCard = (p: Page): Locator => conversation(p).getByRole('article').last();

export const answerLink = (p: Page, name: string): Locator =>
  answerCard(p).getByRole('link', { name: new RegExp(`^${name}`) });

export const suggestion = (p: Page, text: string): Locator =>
  conversation(p).getByRole('button', { name: text, exact: true });

export const askAiButton = (p: Page): Locator =>
  conversation(p).getByRole('button', { name: /^Ask the AI instead/ }).last();

/** Asks a question the router cannot place, then sends it to the AI. */
export async function askAi(p: Page, text: string): Promise<void> {
  await ask(p, text);
  await askAiButton(p).click();
}
```

- [ ] **Step 2: The new spec**

Create `frontend/e2e/specs/assistant/quick-answers.spec.ts`:

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as assistant from '../../screens/assistant.ts';

/**
 * Instant answers end to end (ADR 0036): the real API, the real local model and router, the real
 * caller-scoped queries. "Write me a poem about the rain" is labelled unsure in the evaluation set
 * (tests/Infrastructure.Tests/Assistant/Quick/evaluation.en.json), so CI guarantees it never gets an
 * instant answer.
 */
// @local-only: instant answers are configured on the managed stack, and the AI half needs the fake
// Messages API, which runs only there. Runs as workerUser: no auth permit is spent.
test.describe('instant answers', { tag: '@local-only' }, () => {
  test('a member asks about their last order and opens it from the answer', async ({ signedInPage, api, workerUser }) => {
    const [sku] = await api.placeOrders(workerUser, 1);
    if (sku === undefined) {
      throw new Error('placeOrders returned no sku');
    }

    await test.step('ask', async () => {
      await signedInPage.goto('/assistant');
      await assistant.ask(signedInPage, 'Has my last order shipped?');

      await expect(assistant.answerCard(signedInPage)).toContainText('Your last order has not shipped yet.');
      await expect(assistant.answerCard(signedInPage)).toContainText(sku);
    });

    await test.step('follow the link', async () => {
      await assistant.answerLink(signedInPage, 'Open order').click();

      await expect(signedInPage).toHaveURL(/\/orders\/[0-9a-f-]{36}$/);
    });
  });

  test('a question it cannot place offers suggestions, and the AI answers on request', async ({ signedInPage }) => {
    await signedInPage.goto('/assistant');
    await assistant.ask(signedInPage, 'Write me a poem about the rain');

    await expect(assistant.conversation(signedInPage)).toContainText("I'm not sure what you mean.");

    await assistant.askAiButton(signedInPage).click();

    await expect(assistant.toolsUsed(signedInPage)).toContainText('Checked your orders');
    await expect(assistant.conversation(signedInPage)).toContainText('You have no open orders.');
  });
});
```

- [ ] **Step 3: Keep the existing AI spec on the AI path**

In `frontend/e2e/specs/assistant/assistant.spec.ts`, both questions are now answered instantly. Make each go to the AI explicitly:
- the member test: `const text = \`Write me a poem about the rain ${String(Date.now())}\`;` and `await assistant.askAi(signedInPage, text);` instead of `assistant.ask(...)`;
- the operator test: `await assistant.askAi(adminPage, 'Write me a poem about the rain');`.

Every assertion after the ask stays as it is: the fake picks `my_orders` or `job_health` from the tools the scope offers, whatever the question. Update the file's doc comment: "Questions the router cannot place, sent to the AI with Ask the AI."

- [ ] **Step 4: Run the suite**

Stop any dev loop first (`./scripts/stop-dev.ps1`). Run (from `frontend/`): `npm run e2e`
Expected: everything passes, including the two new tests. If the poem question gets an instant answer, the evaluation gate (Task 13) is wrong or stale: fix it there, never by changing this question.

- [ ] **Step 5: Update the e2e notes, then commit**

In `frontend/e2e/CLAUDE.md`: run `npx playwright test --list` and `npx playwright test --list --grep-invert @local-only` and put both totals into the "A `kind` run therefore executes … of the … tests" sentence (expected: the kind count unchanged, the total up by two). In "What is covered", extend the Assistant row: `; instant answers (an order question answered from the member's own data with a link to the order; an unrecognised question gets suggestions and Ask the AI)`.

```bash
git add frontend/e2e
git commit -m "feat(assistant): cover instant answers and Ask the AI end to end

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 16: Record, verify, review, and open the PR

**Files:**
- Create: `docs/adr/0036-instant-answers-before-the-ai.md`
- Modify: `.claude/skills/assistant/SKILL.md`, `CLAUDE.md` (the Assistant rule), `docs/superpowers/specs/2026-10-12-assistant-instant-answers-design.md` (rulings)

- [ ] **Step 1: The ADR**

Run the `adr` skill with the title `Instant answers before the AI`. Context, decision, consequences and alternatives come from the spec and this plan's "Rulings" section; the decision must state: local in-process model (pinned, hash-checked, bge-small-en-v1.5); in-memory index, no vector database, and the growth path; threshold + margin and the evaluation gate; read-only, answers built from caller-scoped queries only; Claude only on request, through its own endpoint and limits; quick replies stored as two new append-only turn types with fixed replay text; English only. Consequences must include the costs: a 34 MB model in the API image and about 60 MB resident per pod; catalog curation is manual; a question outside the catalog needs a click to reach the AI; the replay text of a quick answer reaches Anthropic if the user later asks the AI in the same conversation (the asker's own data, as ADR 0034 allows). Alternatives: no LLM at all; Claude for every question; keyword rules; a trained classifier; a vector database; a separate embedding service.

- [ ] **Step 2: The assistant skill and CLAUDE.md**

In `.claude/skills/assistant/SKILL.md`, add a section `## Instant answers (ADR 0036)` covering, briefly: where the slice lives (`src/Application/Assistant/Quick`, `src/Infrastructure/Assistant/Quick`); adding an intent (a `QuickIntents` entry, at least 10 phrases including its canonical question, a composer case, evaluation questions — never the same wording as a phrase); adding a help article (front matter + `##` sections, plain text, real button labels); the model (`scripts/fetch-assistant-model.ps1`, `models/assistant`, copied as `assistant-model/`); tuning (`QuickRouting`, the evaluation gate and its floors); the API host alone builds the index; quick replies are `QuickAnswerTurn`/`SuggestionsTurn` with replay text fixed at write time.

In the root `CLAUDE.md`, append to the **Assistant** paragraph under "Rules that fail silently": `Instant answers (ADR 0036) are built from caller-scoped queries only and stored with their replay text fixed at write time; a phrase never repeats an evaluation question, or the precision gate tests the catalog against itself; only the API builds the index.`

- [ ] **Step 3: Bring the spec in line**

In `docs/superpowers/specs/2026-10-12-assistant-instant-answers-design.md`, apply this plan's rulings 1, 2, 4, 5, 7 and 8 where the spec says otherwise (the endpoint split and its limits; no automatic AI fall-through; the in-house corrector; no trigram index yet; start-up build; the sign-out-everywhere article in place of cancelling).

- [ ] **Step 4: Verify**

Invoke the `verify` skill. Expected: backend format, Debug build, every test project, frontend lint/build/tests, the codegen diff (empty), the contract diff (empty after Task 11's regeneration), e2e and hooks all green. Report what ran; a skipped step is not a pass.

- [ ] **Step 5: The image, for real**

Use the `kubernetes` skill to deploy to the kind cluster (`deploy.ps1`), then, signed in as a member on the cluster's URL, ask "Has my last order shipped?" and confirm an instant answer. This is the only check of ONNX Runtime's native library in the chiseled image (Review Focus 4). If kind is not available on this machine, say so in the PR description as "image unverified on kind".

- [ ] **Step 6: Review**

Dispatch the `dotnet-reviewer` agent on `git diff main...HEAD -- src tests` and the `react-reviewer` agent on `git diff main...HEAD -- frontend`; fix what they raise.

- [ ] **Step 7: Commit, then ask before publishing**

```bash
git add docs .claude/skills/assistant/SKILL.md CLAUDE.md
git commit -m "docs(assistant): record instant answers before the AI

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

Ask the user before pushing. Then: `git push -u origin claude/assistant-instant-answers` and `gh pr create --base main --title "feat(assistant): instant answers before the AI"` with a body that has a summary, the evaluation numbers from Task 13, the rulings, what was and was not verified (kind), and ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. After it merges, remind the user that PR 2 (feedback and insights) is next.

---

## Self-Review

**Spec coverage.** Architecture (port, adapter, in-memory index, router, resolver, composer) → Tasks 2, 4, 6, 7. Catalog, intents, help, CI catalog tests → Task 3. Spelling (correction pass from day one, fuzzy names) → Tasks 5 and 6. Turn types, replay, display → Task 8. Command, limits (50 total, 20 AI), output events → Task 9. Start-up build, API-only, readiness untouched, configuration → Task 10. Endpoint, status `{quick, ai}`, rate limits, saved cards, contract → Task 11. Frontend cards, badges, chips, Ask the AI, link allow-list, icon shown for either half → Task 12. Evaluation gate with typo section, thresholds from data → Task 13. Model delivery in CI, image, e2e, kind → Tasks 1 and 14. E2E → Task 15. ADR 0036, docs, verify, image check, review, PR, PR 2 reminder → Task 16. Out of scope (Danish, actions, in-app editing, vector database, training, ratings/outcome log/insights) is not built.

**Placeholders.** None in code steps. Two values are produced by running something, with the exact procedure written: the tuned `QuickRouting.Default` and the evaluation floors (Task 13, Steps 4–5), and the e2e totals (Task 15, Step 5).

**Type consistency.** `ITextEmbedder.Embed` → `Result<float[]>` everywhere. `QuickCatalog(Phrases, Help)`, `HelpSection(ArticleId, Title, Question, Heading, Text, Link)`, `QuickAnswer(Kind, Title, Text, Facts, Table, Link, AnsweredAs)`, `QuickSuggestions(Text, Options)` match across Tasks 3–12. `QuickIndexBuilder.Build` returns `(Index, Spelling)` (Tasks 4, 9, 10, 13). `IntentRouter(QuickIndexState, ITextEmbedder, QuickRouting).Route(question, scope)` (Tasks 4, 9, 10, 13). `QuickAnswerComposer(IQueryDispatcher, ParameterResolver).ComposeAsync(intent, question, answeredAs, ct)` and `Help(section, answeredAs)` (Tasks 7, 9). `AssistantTurnCodec.Quick`/`Suggested` (Task 8). `AssistantAvailability(AiEnabled, QuickEnabled, HeartbeatInterval)` (Tasks 9–11). SSE event `type`s `answer`/`suggestions` with `answer`/`suggestions` payloads (Tasks 9, 11, 12, 15). Card `article` names "Instant answer"/"Help answer" (Tasks 12, 15). The link allow-list is the same ten patterns in `QuickCatalogTests` and `quickLinks.ts`.
