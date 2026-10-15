# Assistant Feedback and Insights Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every assistant answer can be rated 👍/👎, every answered question leaves one outcome row (no question text, no user id), and administrators get an insights page with the instant-answer rate over time and a scrubbed list of the questions the assistant missed.

**Architecture:** Each answer is identified by the sequence number of the stored turn that carries it (a quick answer's turn, or an AI answer's final model turn). The handlers already know those numbers before the commit, so they put them on the SSE events and in a new outcome row, and the saved-conversation view reports them with the owner's rating. Ratings and outcomes are two new tables that cascade with their conversation, so the 30-day retention and the owner's delete remove them too. The insights query aggregates outcomes and reads the missed questions' text live from the transcript, through `OperationalText.Scrub`.

**Tech Stack:** .NET 10 (C# 14), EF Core + Npgsql, FluentValidation, xUnit + FluentAssertions + NSubstitute, Testcontainers; React 19 + TypeScript 6, TanStack Query, Vitest + MSW, Playwright.

**Spec:** `docs/superpowers/specs/2026-10-12-assistant-instant-answers-design.md`, section **Feedback and insights (PR 2)**, plus its Tests and PR split sections. Rulings agreed with the user on 2026-10-15 (they replace the spec where it differs):

1. `turn` is added to the SSE `answer`, `suggestions` and `done` events, and to each saved message in `GET /api/assistant/conversations/{id}`, so a reopened conversation can be rated.
2. An AI answer is rated on its **final model turn** (the one carrying the answer text). A quick answer or a suggestions reply is rated on its single stored turn.
3. Only answered-and-saved questions get an outcome row. A `503 assistant.quick_unavailable` stores nothing, so it logs nothing.
4. ADR 0037 *Administrators read missed assistant questions* amends ADR 0034.

## Global Constraints

- PR type `feat`, title `feat(assistant): rate answers and show administrators what the assistant missed`. Every commit subject `feat(assistant): …` (docs-only commits `docs(assistant): …`); every message ends with a `Co-Authored-By:` trailer naming the model that wrote the commit. Branch `claude/assistant-feedback-insights` (exists). PR against `main` only.
- **No question text and no user id** in `assistant_ratings` or `assistant_outcomes`. Question text is read live from `assistant_turns` and passed through `OperationalText.Scrub`; no copy is stored anywhere.
- Ratings and outcomes are **deleted with their conversation** (`ON DELETE CASCADE`), so the owner's delete and the 30-day retention remove them; the insights page covers at most the last 30 days.
- Ratings are **owner only**: another user's conversation is a 404, identical to a missing one. Insights are **administrators only** (`AuthorizationPolicies.Monitoring.Read` on the controller).
- Transcripts stay **append-only** (ADR 0033): nothing here writes, rewrites or re-serializes an `assistant_turns` row.
- Rating values on the wire: `"Up"`, `"Down"`, or `null` to clear (the API serializes enums as strings).
- Warnings are errors; nullable on; **never `catch (Exception)`**; `ConfigureAwait(false)` on every await in `src/`.
- Application references Infrastructure never; Api names Infrastructure types only in composition (`ArchitectureTests`).
- New commands, queries and validators are registered in `InfrastructureRegistration.RegisterAssistant` (`RegistrationCompletenessTests` fails otherwise).
- Migrations are generated with `dotnet ef` (`src/Infrastructure` is both project and startup project, `ConnectionStrings__Default` in the environment), never hand-edited once committed.
- Any controller or DTO change regenerates `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` (the `regenerate` skill) in the same task.
- Frontend: function components, explicit prop interfaces and return types, no `any`, no `!`, `eslint --max-warnings 0`; a new page is added to `src/navigation.ts` or it is reachable only by URL; every query and mutation renders its error.
- E2E: import `test`/`expect` from `../../fixtures/index.ts`; role/label locators only; assertions only in specs; the new spec is `@local-only`; no new auth permit is spent.
- Run backend commands from the repo root, frontend commands from `frontend/`. Run the .NET test projects one at a time.

## Review Focus

1. **Rating something that is not an answer** (a question turn, a tool-round model turn, a turn past the end): 400 `assistant.not_an_answer` or 404, nothing stored. Tested in Task 3 (store) and Task 5 (endpoint).
2. **Rating another user's conversation**: a 404 indistinguishable from a missing conversation, nothing stored. Tested in Task 3 and Task 5.
3. **A deleted or expired conversation**: its ratings and outcomes are gone, and the misses list no longer shows its question. Tested in Task 3 (cascade) and Task 7 (reader).
4. **A missed question that contains an email or an IP address**: shown scrubbed (`[email]`, `[ip]`), never verbatim. Tested in Task 7.
5. **Rating while an answer streams, and clicking the same rating twice**: no buttons until the answer's turn is known; a second click on the chosen rating clears it. Tested in Task 8.

---

## Task 1: The router reports its evidence

**Files:**
- Modify: `src/Application/Assistant/Quick/IntentRouter.cs`
- Test: `tests/Application.Tests/Assistant/Quick/IntentRouterEvidenceTests.cs` (new), `tests/Application.Tests/Assistant/Quick/IntentRouterTests.cs` (one assertion)

**Interfaces:**
- Produces: `public sealed record RouteEvidence(double BestScore, double Margin, bool Corrected)` with `static RouteEvidence None`; `RouteDecision.Evidence` (an `init` property on the abstract base, default `RouteEvidence.None`). Task 4 reads it.

- [ ] **Step 1: Write the failing tests**

Create `tests/Application.Tests/Assistant/Quick/IntentRouterEvidenceTests.cs`:

```csharp
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class IntentRouterEvidenceTests
{
    private static readonly QuickRouting Routing = new(0.8, 0.05, 0.5);

    private readonly QuickIndexState _state = new();
    private readonly FakeEmbedder _embedder = new FakeEmbedder()
        .Add("Show my recent orders", 1, 0)
        .Add("Is my export ready?", 0, 1);

    public IntentRouterEvidenceTests()
    {
        var catalog = new QuickCatalog(
            [new(QuickIntents.RecentOrders, "Show my recent orders"), new(QuickIntents.ExportStatus, "Is my export ready?")], []);
        var built = QuickIndexBuilder.Build(catalog, _embedder).Value;
        _state.Publish(built.Index, built.Spelling);
    }

    private IntentRouter Router => new(_state, _embedder, Routing);

    [Fact]
    public void Route_AnInstantMatch_ReportsItsBestScoreAndMargin()
    {
        _embedder.Add("show my orders", 1, 0.05f);

        var decision = Router.Route("show my orders", ToolScope.Member).Value;

        decision.Should().BeOfType<RouteToIntent>();
        decision.Evidence.BestScore.Should().BeApproximately(0.9988, 0.001);
        decision.Evidence.Margin.Should().BeApproximately(0.9489, 0.001);
        decision.Evidence.Corrected.Should().BeFalse();
    }

    [Fact]
    public void Route_AnUnsureQuestion_StillReportsTheBestScoreAndMargin()
    {
        _embedder.Add("orders or export", 1, 1);

        var decision = Router.Route("orders or export", ToolScope.Member).Value;

        decision.Should().BeOfType<RouteUnsure>();
        decision.Evidence.BestScore.Should().BeApproximately(0.7071, 0.001);
        decision.Evidence.Margin.Should().BeApproximately(0, 0.001);
    }
}
```

In `tests/Application.Tests/Assistant/Quick/IntentRouterTests.cs`, in `Route_UsesTheSpellingCorrection_WhenItYieldsAnInstantMatchTheOriginalDidNot`, after its existing assertions add:

```csharp
        decision.Evidence.Corrected.Should().BeTrue();
```

(use the variable that test already holds the routed decision in).

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~IntentRouter" --nologo`
Expected: build FAILS — `RouteDecision` has no `Evidence`.

- [ ] **Step 3: Add the evidence**

In `src/Application/Assistant/Quick/IntentRouter.cs`, replace the `RouteDecision` declaration and its pragma pair with:

```csharp
/// <summary>
/// How the router reached a decision, for the outcome log (PR 2): the best match's similarity,
/// its lead over the runner-up, and whether the spelling correction won. Never the question.
/// </summary>
public sealed record RouteEvidence(double BestScore, double Margin, bool Corrected)
{
    public static RouteEvidence None { get; } = new(0, 0, false);
}

public abstract record RouteDecision
{
    public RouteEvidence Evidence { get; init; } = RouteEvidence.None;
}
```

(The `#pragma warning disable/restore S2094` pair goes: the record is no longer empty, and a suppression with nothing to suppress is itself a warning.)

Replace `Decide` with:

```csharp
    private (RouteDecision Decision, double Score) Decide(IReadOnlyList<QuickMatch> matches, string? answeredAs)
    {
        if (matches.Count == 0)
        {
            return (new RouteUnsure([]) { Evidence = RouteEvidence.None with { Corrected = answeredAs is not null } }, 0);
        }

        var top = matches[0];
        var runnerUp = matches.Count > 1 ? matches[1].Score : 0;
        var evidence = new RouteEvidence(top.Score, top.Score - runnerUp, answeredAs is not null);
        if (top.Score >= routing.Threshold && top.Score - runnerUp >= routing.Margin)
        {
            RouteDecision decision = top.Target.Kind == QuickTargetKind.Intent
                ? new RouteToIntent(QuickIntents.Find(top.Target.Id) ?? throw new InvalidOperationException(top.Target.Id), top.Score, answeredAs)
                : new RouteToHelp(top.Section ?? throw new InvalidOperationException(top.Target.Id), top.Score, answeredAs);
            return (decision with { Evidence = evidence }, top.Score);
        }

        var suggestions = matches
            .Where(m => m.Score >= routing.SuggestionFloor)
            .Take(MaxSuggestions)
            .Select(m => m.Target.Kind == QuickTargetKind.Intent
                ? QuickIntents.Find(m.Target.Id)?.CanonicalQuestion
                : m.Section?.Question)
            .OfType<string>()
            .ToList();

        return (new RouteUnsure(suggestions) { Evidence = evidence }, top.Score);
    }
```

`Route` itself does not change: the decision it returns already carries its evidence.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~Quick" --nologo`
Expected: PASS, the existing router, handler and evaluation-free tests included.

- [ ] **Step 5: Commit**

```bash
git add src/Application/Assistant/Quick/IntentRouter.cs tests/Application.Tests/Assistant/Quick
git commit -m "feat(assistant): record how the router reached each decision"
```

---

## Task 2: Every answer carries its turn

**Files:**
- Modify: `src/Application/Assistant/AskAssistant.cs` (`AssistantAnswer`, the handler), `src/Application/Assistant/Quick/AskQuick.cs`, `src/Application/Assistant/AssistantPorts.cs` (`IAssistantOutput`), `src/Infrastructure/Assistant/NoAssistantOutput.cs`, `src/Api/Assistant/SseAssistantOutput.cs`, `src/Api/Assistant/AssistantDtos.cs` (`AssistantEventResponse`)
- Regenerate: `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts`
- Test: `tests/Application.Tests/Assistant/Quick/AskQuickHandlerTests.cs`, `tests/Application.Tests/Assistant/AskAssistantHandlerTests.cs`, `tests/Api.IntegrationTests/Assistant/AssistantQuickEndpointTests.cs`

**Interfaces:**
- Produces: `AssistantAnswer(Guid ConversationId, AssistantOutcome Outcome, int Turn)`; `IAssistantOutput.AnswerAsync(QuickAnswer answer, int turn, CancellationToken)`, `IAssistantOutput.SuggestionsAsync(QuickSuggestions suggestions, int turn, CancellationToken)`; `AssistantEventResponse.Turn` (`int?`), set on `answer`, `suggestions` and `done`.
- The turn is the `Seq` the answer's stored turn will get: `IAssistantConversations.Append` numbers turns from the conversation's current turn count, which is `AssistantConversation.Turns.Count` (0 for a new conversation). A quick reply is at `Turns.Count + 1` (after its question); an AI answer's final model turn is at `Turns.Count + added.Count - 1`.

- [ ] **Step 1: Write the failing tests**

In `AskQuickHandlerTests.cs`, change the existing `AMatchedQuestion_IsAnsweredWrittenAndStored` output assertion to:

```csharp
        await _output.Received(1).AnswerAsync(Arg.Is<QuickAnswer>(a => a.Text == "You haven't placed any orders yet."), 1, Arg.Any<CancellationToken>());
        result.Value.Turn.Should().Be(1);
```

and the `AnUnsureQuestion_GetsSuggestions_AndStoresThem` one to pass `1` as the turn argument (`SuggestionsAsync(Arg.Is<QuickSuggestions>(…), 1, Arg.Any<CancellationToken>())`). Then add:

```csharp
    [Fact]
    public async Task AQuestionInAnExistingConversation_IsAnsweredOnTheTurnAfterItsQuestion()
    {
        _embedder.Add("show my orders", 1, 0.05f);
        var id = Guid.NewGuid();
        _conversations.FindAsync(id, Arg.Any<CancellationToken>()).Returns(new AssistantConversation(
            id, Caller, 1, [new UserQuestionTurn("earlier", Now), new QuickAnswerTurn(new QuickAnswer(QuickAnswerKind.Instant, null, "Earlier.", [], null, null, null), "Earlier.")]));

        var result = await AskAsync("show my orders", id);

        result.Value.Turn.Should().Be(3);
        await _output.Received(1).AnswerAsync(Arg.Any<QuickAnswer>(), 3, Arg.Any<CancellationToken>());
    }
```

(If `QuickAnswer`'s constructor has a different parameter list than the seven above, build the same "Earlier." instant answer with the one the code has; the test is about the turn number.)

In `AskAssistantHandlerTests.cs` add:

```csharp
    [Fact]
    public async Task HandleAsync_AnAnswerAfterAToolRound_IsOnItsFinalModelTurn()
    {
        ModelReturns(ToolUse(), Answer());

        var result = await AskAsync();

        // question 0, tool_use model 1, tool results 2, final model 3
        result.Value.Turn.Should().Be(3);
    }

    [Fact]
    public async Task HandleAsync_InAnExistingConversation_CountsTheStoredTurnsFirst()
    {
        ModelReturns(Answer());
        var id = Guid.NewGuid();
        _conversations.FindAsync(id, Arg.Any<CancellationToken>()).Returns(new AssistantConversation(
            id, Caller, 1, [new UserQuestionTurn("earlier", Now), new ModelTurn("earlier answer")]));

        var result = await AskAsync(id);

        result.Value.Turn.Should().Be(3);
    }
```

In `AssistantQuickEndpointTests.cs` add (it reuses that class's `MemberAsync` and `EventsAsync` helpers):

```csharp
    [Fact]
    public async Task Quick_PutsTheAnswersTurnOnItsEventsAndOnDone()
    {
        var client = await MemberAsync();

        var events = await EventsAsync(await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Has my last order shipped?" }));

        events.Should().HaveCount(2);
        events[0].GetProperty("turn").GetInt32().Should().Be(1);
        events[1].GetProperty("type").GetString().Should().Be("done");
        events[1].GetProperty("turn").GetInt32().Should().Be(1);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build --nologo -v q`
Expected: FAIL — `AssistantAnswer` has no `Turn`, and `AnswerAsync`/`SuggestionsAsync` take no turn.

- [ ] **Step 3: Thread the turn through**

`src/Application/Assistant/AskAssistant.cs`:

```csharp
/// <summary>
/// A saved answer: its conversation, how it ended, and the turn that carries it — the one a rating
/// names (an AI answer's final model turn).
/// </summary>
public sealed record AssistantAnswer(Guid ConversationId, AssistantOutcome Outcome, int Turn);
```

In `AskAssistantHandler.HandleAsync`, replace the final two statements with:

```csharp
        conversations.Append(opened.Value.Id, added, now);

        // Append numbers the turns from the stored count, so the last one added — the final model
        // turn, which carries the answer — is at this position.
        var answerTurn = opened.Value.Turns.Count + added.Count - 1;
        return Result.Success(new AssistantAnswer(opened.Value.Id, outcome.Value, answerTurn));
```

`src/Application/Assistant/AssistantPorts.cs`, in `IAssistantOutput`:

```csharp
    /// <summary>A quick answer, all at once: nothing to stream. <paramref name="turn"/> is the turn it is stored on.</summary>
    public Task AnswerAsync(QuickAnswer answer, int turn, CancellationToken cancellationToken);

    public Task SuggestionsAsync(QuickSuggestions suggestions, int turn, CancellationToken cancellationToken);
```

`src/Application/Assistant/Quick/AskQuick.cs`, in `HandleAsync` after `ReplyAsync`:

```csharp
        // The reply is stored on the turn after its question.
        var answerTurn = opened.Value.Turns.Count + 1;
        if (reply is QuickAnswer answer)
        {
            await output.AnswerAsync(answer, answerTurn, cancellationToken).ConfigureAwait(false);
        }
        else if (reply is QuickSuggestions suggestions)
        {
            await output.SuggestionsAsync(suggestions, answerTurn, cancellationToken).ConfigureAwait(false);
        }

        conversations.Append(opened.Value.Id, [new UserQuestionTurn(question, now), QuickReplay.Turn(reply)], now);
        return Result.Success(new AssistantAnswer(opened.Value.Id, AssistantOutcome.Answered, answerTurn));
```

`src/Infrastructure/Assistant/NoAssistantOutput.cs`: add the `int turn` parameter to both methods (bodies stay `Task.CompletedTask`).

`src/Api/Assistant/AssistantDtos.cs`, in `AssistantEventResponse`:

```csharp
    /// <summary>The stored turn that carries this answer: what a rating names. On answer, suggestions and done.</summary>
    public int? Turn { get; init; }

    public static AssistantEventResponse Answered(AssistantAnswerResponse answer, int turn) => new() { Type = "answer", Answer = answer, Turn = turn };

    public static AssistantEventResponse Suggested(AssistantSuggestionsResponse suggestions, int turn) => new() { Type = "suggestions", Suggestions = suggestions, Turn = turn };

    public static AssistantEventResponse Done(AssistantAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        return new() { Type = "done", ConversationId = answer.ConversationId, Outcome = answer.Outcome, Turn = answer.Turn };
    }
```

(replacing the existing `Answered`, `Suggested` and `Done`), and extend the class summary's list with "answer, suggestions and done carry the answer's turn".

`src/Api/Assistant/SseAssistantOutput.cs`:

```csharp
    public Task AnswerAsync(QuickAnswer answer, int turn, CancellationToken cancellationToken) =>
        WriteAsync(AssistantEventResponse.Answered(AssistantCards.ToResponse(answer), turn), cancellationToken);

    public Task SuggestionsAsync(QuickSuggestions suggestions, int turn, CancellationToken cancellationToken) =>
        WriteAsync(AssistantEventResponse.Suggested(AssistantCards.ToResponse(suggestions, availability.AiEnabled), turn), cancellationToken);
```

Fix every other call site the compiler names (test substitutes and fakes construct `AssistantAnswer` or call these two methods): pass the turn the scenario implies, never a placeholder.

- [ ] **Step 4: Regenerate the contract**

Follow the `regenerate` skill's contract steps (`dotnet restore src/Api`, then `dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"` with `ConnectionStrings__Default=…`, `Wolverine__Durable=false`, `Admin__ReconcileOnStart=false`, then `npm run generate:api` from `frontend/`). Expected: `AssistantEventResponse` gains `turn`. Run its codegen diff check too: no Wolverine handler changed, so both trees must be unchanged.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Application.Tests --nologo`, then `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~Assistant" --nologo`, then from `frontend/`: `npx tsc --noEmit -p tsconfig.app.json`.
Expected: all PASS (the frontend still compiles: `turn` is a new optional field).

- [ ] **Step 6: Commit**

```bash
git add src tests openapi frontend/src/api/schema.d.ts
git commit -m "feat(assistant): put the answer's turn on its events and on done"
```

---

## Task 3: Where ratings and outcomes are kept

**Files:**
- Create: `src/Application/Assistant/AssistantFeedback.cs`, `src/Infrastructure/Assistant/AnswerTurns.cs`, `src/Infrastructure/Assistant/AssistantFeedbackRows.cs`, `src/Infrastructure/Assistant/AssistantRatings.cs`, `src/Infrastructure/Assistant/AssistantOutcomeLog.cs`, `src/Infrastructure/Persistence/Configurations/AssistantRatingRowConfiguration.cs`, `src/Infrastructure/Persistence/Configurations/AssistantOutcomeRowConfiguration.cs`, a migration `src/Infrastructure/Persistence/Migrations/*_AddAssistantFeedback.cs` (generated)
- Modify: `src/Infrastructure/Persistence/AiFrameworkDbContext.cs` (two `DbSet`s), `src/Infrastructure/InfrastructureRegistration.cs` (two ports)
- Test: `tests/Infrastructure.Tests/Assistant/AnswerTurnsTests.cs`, `tests/Infrastructure.Tests/Assistant/AssistantFeedbackStoreTests.cs`

**Interfaces:**
- Produces (Application, namespace `AiFramework.Application.Assistant`):
  - `enum AnswerRating { Up, Down }`; `enum AnswerPath { Instant, Help, Suggested, Ai }`; `enum RateResult { Rated, NotFound, NotAnAnswer }`
  - `record AnsweredQuestion(Guid ConversationId, int QuestionTurn, int AnswerTurn, DateTimeOffset At, AnswerPath Path, string? TargetId, double? BestScore, double? Margin, bool Corrected, ToolScope Scope)`
  - `interface IAssistantOutcomeLog { void Record(AnsweredQuestion question); }`
  - `interface IAssistantRatings { Task<RateResult> RateAsync(Guid conversationId, Guid ownerId, int turn, AnswerRating? rating, DateTimeOffset at, CancellationToken cancellationToken); }`
- Produces (Infrastructure): `AnswerTurns.Of(IEnumerable<(int Seq, string Kind)> turns) : IReadOnlySet<int>`; `AssistantRatingRow`, `AssistantOutcomeRow`; `AiFrameworkDbContext.AssistantRatings`, `.AssistantOutcomes`.

- [ ] **Step 1: Write the failing tests**

`tests/Infrastructure.Tests/Assistant/AnswerTurnsTests.cs` (no database):

```csharp
using AiFramework.Infrastructure.Assistant;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Assistant;

public sealed class AnswerTurnsTests
{
    private const string Q = AssistantTurnCodec.Question;
    private const string M = AssistantTurnCodec.Model;
    private const string R = AssistantTurnCodec.ToolResults;

    [Fact]
    public void Of_AnAiAnswerAfterAToolRound_IsItsFinalModelTurn()
    {
        AnswerTurns.Of([(0, Q), (1, M), (2, R), (3, M)]).Should().Equal(3);
    }

    [Fact]
    public void Of_QuickRepliesAreAnswersOnTheirOwnTurn()
    {
        AnswerTurns.Of([(0, Q), (1, AssistantTurnCodec.Quick), (2, Q), (3, AssistantTurnCodec.Suggested)]).Should().Equal(1, 3);
    }

    [Fact]
    public void Of_AModelTurnFollowedByToolResultsIsNotAnAnswer()
    {
        AnswerTurns.Of([(0, Q), (1, M), (2, R), (3, M), (4, Q), (5, M)]).Should().Equal(3, 5);
    }
}
```

`tests/Infrastructure.Tests/Assistant/AssistantFeedbackStoreTests.cs`:

```csharp
using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Assistant;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Assistant;

[Collection(nameof(PostgresCollection))]
public sealed class AssistantFeedbackStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    // question 0, tool_use 1, tool results 2, final model 3: the answer is turn 3.
    private static readonly AssistantTurn[] ToolRound =
    [
        new UserQuestionTurn("Did jobs fail?", Now),
        new ModelTurn("""[{"type":"tool_use","id":"toolu_1","name":"job_health","input":{"window_hours":24}}]"""),
        new ToolResultsTurn([new AssistantToolResult("toolu_1", """{"failed":3}""", false)]),
        new ModelTurn("""[{"type":"text","text":"Three failed."}]"""),
    ];

    private async Task<Guid> SeedAsync(Guid owner)
    {
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        var store = new AssistantConversations(context);
        store.Start(id, owner, "Did jobs fail?", Now);
        store.Append(id, ToolRound, Now);
        await context.SaveChangesAsync();
        return id;
    }

    private async Task<RateResult> RateAsync(Guid id, Guid owner, int turn, AnswerRating? rating)
    {
        await using var context = fixture.CreateContext();
        var result = await new AssistantRatings(context).RateAsync(id, owner, turn, rating, Now, CancellationToken.None);
        await context.SaveChangesAsync();
        return result;
    }

    private async Task<AnswerRating?> StoredAsync(Guid id, int turn)
    {
        await using var context = fixture.CreateContext();
        return (await context.AssistantRatings.AsNoTracking().SingleOrDefaultAsync(r => r.ConversationId == id && r.Turn == turn))?.Rating;
    }

    [Fact]
    public async Task RateAsync_TheAnswerTurn_StoresTheRating()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner);

        (await RateAsync(id, owner, 3, AnswerRating.Down)).Should().Be(RateResult.Rated);

        (await StoredAsync(id, 3)).Should().Be(AnswerRating.Down);
    }

    [Fact]
    public async Task RateAsync_Again_ReplacesTheRating_AndNullClearsIt()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner);
        await RateAsync(id, owner, 3, AnswerRating.Down);

        await RateAsync(id, owner, 3, AnswerRating.Up);
        (await StoredAsync(id, 3)).Should().Be(AnswerRating.Up);

        await RateAsync(id, owner, 3, null);
        (await StoredAsync(id, 3)).Should().BeNull();
    }

    [Theory]
    [InlineData(0)] // the question
    [InlineData(1)] // a tool-round model turn
    [InlineData(2)] // tool results
    public async Task RateAsync_ATurnThatIsNotAnAnswer_IsRefused_AndStoresNothing(int turn)
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner);

        (await RateAsync(id, owner, turn, AnswerRating.Up)).Should().Be(RateResult.NotAnAnswer);

        (await StoredAsync(id, turn)).Should().BeNull();
    }

    [Fact]
    public async Task RateAsync_ATurnPastTheEnd_IsNotFound()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner);

        (await RateAsync(id, owner, 9, AnswerRating.Up)).Should().Be(RateResult.NotFound);
    }

    [Fact]
    public async Task RateAsync_AnotherOwnersConversation_IsNotFound_AndStoresNothing()
    {
        var id = await SeedAsync(Guid.NewGuid());

        (await RateAsync(id, Guid.NewGuid(), 3, AnswerRating.Up)).Should().Be(RateResult.NotFound);

        (await StoredAsync(id, 3)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingTheConversation_RemovesItsRatingsAndOutcomes()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner);
        await RateAsync(id, owner, 3, AnswerRating.Down);
        await using (var context = fixture.CreateContext())
        {
            new AssistantOutcomeLog(context).Record(new AnsweredQuestion(
                id, 0, 3, Now, AnswerPath.Ai, null, null, null, false, ToolScope.Operator));
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.CreateContext())
        {
            (await new AssistantHistoryStore(context).DeleteAsync(id, owner, CancellationToken.None)).Should().BeTrue();
            await context.SaveChangesAsync();
        }

        await using var check = fixture.CreateContext();
        (await check.AssistantRatings.AnyAsync(r => r.ConversationId == id)).Should().BeFalse();
        (await check.AssistantOutcomes.AnyAsync(o => o.ConversationId == id)).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build tests/Infrastructure.Tests --nologo -v q`
Expected: FAIL — `AnswerTurns`, `AssistantRatings`, `AssistantOutcomeLog`, the enums and the `DbSet`s do not exist.

- [ ] **Step 3: The Application side**

`src/Application/Assistant/AssistantFeedback.cs`:

```csharp
namespace AiFramework.Application.Assistant;

public enum AnswerRating
{
    Up,
    Down,
}

/// <summary>Which path answered a question: an instant answer, a help article, suggestions, or the AI.</summary>
public enum AnswerPath
{
    Instant,
    Help,
    Suggested,
    Ai,
}

public enum RateResult
{
    Rated,

    /// <summary>No such conversation for this owner, or no such turn: indistinguishable on purpose.</summary>
    NotFound,

    /// <summary>The turn exists but carries no answer (a question, a tool round).</summary>
    NotAnAnswer,
}

/// <summary>
/// One answered question, for the insights page. Holds no question text and no user id: the text
/// is read live from the transcript, so it goes when the conversation goes (ADR 0037).
/// </summary>
public sealed record AnsweredQuestion(
    Guid ConversationId,
    int QuestionTurn,
    int AnswerTurn,
    DateTimeOffset At,
    AnswerPath Path,
    string? TargetId,
    double? BestScore,
    double? Margin,
    bool Corrected,
    ToolScope Scope);

/// <summary>Tracked and committed by the unit of work with the question it describes.</summary>
public interface IAssistantOutcomeLog
{
    public void Record(AnsweredQuestion question);
}

/// <summary>The owner's 👍/👎 per answer. Kept beside the transcript, never in it (ADR 0033).</summary>
public interface IAssistantRatings
{
    /// <summary>A null rating clears the answer's rating.</summary>
    public Task<RateResult> RateAsync(
        Guid conversationId, Guid ownerId, int turn, AnswerRating? rating, DateTimeOffset at, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: The rows, their mapping and the migration**

`src/Infrastructure/Assistant/AnswerTurns.cs`:

```csharp
namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// Which stored turns carry an answer, and so may be rated: a quick reply's own turn, and a model
/// turn that ends its question — the last turn of the conversation, or one followed by the next
/// question. A model turn followed by tool results is a step on the way, never an answer.
/// </summary>
internal static class AnswerTurns
{
    public static IReadOnlySet<int> Of(IEnumerable<(int Seq, string Kind)> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var ordered = turns.OrderBy(static t => t.Seq).ToList();
        var answers = new SortedSet<int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (seq, kind) = ordered[i];
            var next = i + 1 < ordered.Count ? ordered[i + 1].Kind : null;
            if (kind is AssistantTurnCodec.Quick or AssistantTurnCodec.Suggested
                || (kind == AssistantTurnCodec.Model && next is null or AssistantTurnCodec.Question))
            {
                answers.Add(seq);
            }
        }

        return answers;
    }
}
```

`src/Infrastructure/Assistant/AssistantFeedbackRows.cs`:

```csharp
using AiFramework.Application.Assistant;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>The owner's rating of one answer. Deleted with its conversation.</summary>
public sealed class AssistantRatingRow
{
    public required Guid ConversationId { get; init; }

    /// <summary>The <see cref="AssistantTurnRow.Seq"/> of the answer's turn.</summary>
    public required int Turn { get; init; }

    public required AnswerRating Rating { get; set; }

    public required DateTimeOffset RatedAt { get; set; }
}

/// <summary>One answered question's routing outcome. No question text, no user id (ADR 0037).</summary>
public sealed class AssistantOutcomeRow
{
    public required Guid Id { get; init; }

    public required Guid ConversationId { get; init; }

    public required int QuestionTurn { get; init; }

    public required int AnswerTurn { get; init; }

    public required DateTimeOffset At { get; init; }

    public required AnswerPath Path { get; init; }

    /// <summary>The intent id or help article id the router chose; null for the AI and for nothing.</summary>
    public string? TargetId { get; init; }

    public double? BestScore { get; init; }

    public double? Margin { get; init; }

    public required bool Corrected { get; init; }

    public required ToolScope Scope { get; init; }
}
```

`src/Infrastructure/Persistence/Configurations/AssistantRatingRowConfiguration.cs`:

```csharp
using AiFramework.Infrastructure.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class AssistantRatingRowConfiguration : IEntityTypeConfiguration<AssistantRatingRow>
{
    public void Configure(EntityTypeBuilder<AssistantRatingRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("assistant_ratings");
        builder.HasKey(r => new { r.ConversationId, r.Turn });
        builder.Property(r => r.Rating).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(r => r.RatedAt).IsRequired();

        // Gone with the conversation: the owner's delete and the 30-day retention remove it too.
        builder.HasOne<AssistantConversationRow>()
            .WithMany()
            .HasForeignKey(r => r.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

`src/Infrastructure/Persistence/Configurations/AssistantOutcomeRowConfiguration.cs`:

```csharp
using AiFramework.Infrastructure.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiFramework.Infrastructure.Persistence.Configurations;

internal sealed class AssistantOutcomeRowConfiguration : IEntityTypeConfiguration<AssistantOutcomeRow>
{
    public void Configure(EntityTypeBuilder<AssistantOutcomeRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("assistant_outcomes");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Path).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(o => o.TargetId).HasMaxLength(64);
        builder.Property(o => o.Scope).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(o => o.At).IsRequired();

        // The insights page reads by time.
        builder.HasIndex(o => o.At).HasDatabaseName("IX_AssistantOutcomes_At");

        builder.HasOne<AssistantConversationRow>()
            .WithMany()
            .HasForeignKey(o => o.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

In `AiFrameworkDbContext.cs`, beside `AssistantTurns`:

```csharp
    public DbSet<AssistantRatingRow> AssistantRatings => Set<AssistantRatingRow>();

    public DbSet<AssistantOutcomeRow> AssistantOutcomes => Set<AssistantOutcomeRow>();
```

Generate the migration (bash, repo root):

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=x;Password=y' \
  dotnet ef migrations add AddAssistantFeedback --project src/Infrastructure --startup-project src/Infrastructure
```

Expected: two `CreateTable` calls (`assistant_ratings`, `assistant_outcomes`) with cascading foreign keys to `assistant_conversations`, the `IX_AssistantOutcomes_At` index, and an index on `assistant_outcomes.ConversationId`. Do not edit it.

- [ ] **Step 5: The two stores**

`src/Infrastructure/Assistant/AssistantOutcomeLog.cs`:

```csharp
using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Persistence;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>Tracked only: the unit of work commits the outcome with the question's turns.</summary>
internal sealed class AssistantOutcomeLog(AiFrameworkDbContext context) : IAssistantOutcomeLog
{
    public void Record(AnsweredQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        context.AssistantOutcomes.Add(new AssistantOutcomeRow
        {
            Id = Guid.NewGuid(),
            ConversationId = question.ConversationId,
            QuestionTurn = question.QuestionTurn,
            AnswerTurn = question.AnswerTurn,
            At = question.At,
            Path = question.Path,
            TargetId = question.TargetId,
            BestScore = question.BestScore,
            Margin = question.Margin,
            Corrected = question.Corrected,
            Scope = question.Scope,
        });
    }
}
```

`src/Infrastructure/Assistant/AssistantRatings.cs`:

```csharp
using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// Rates an answer through the change tracker; the unit of work commits it. Reads only the turns'
/// numbers and kinds, never their payloads, and writes nothing to the transcript.
/// </summary>
internal sealed class AssistantRatings(AiFrameworkDbContext context) : IAssistantRatings
{
    public async Task<RateResult> RateAsync(
        Guid conversationId, Guid ownerId, int turn, AnswerRating? rating, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var owned = await context.AssistantConversations
            .AsNoTracking()
            .AnyAsync(c => c.Id == conversationId && c.OwnerId == ownerId, cancellationToken)
            .ConfigureAwait(false);
        if (!owned)
        {
            return RateResult.NotFound;
        }

        var turns = await context.AssistantTurns
            .AsNoTracking()
            .Where(t => t.ConversationId == conversationId)
            .Select(t => new { t.Seq, t.Kind })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!turns.Any(t => t.Seq == turn))
        {
            return RateResult.NotFound;
        }

        if (!AnswerTurns.Of(turns.Select(t => (t.Seq, t.Kind))).Contains(turn))
        {
            return RateResult.NotAnAnswer;
        }

        var existing = await context.AssistantRatings
            .SingleOrDefaultAsync(r => r.ConversationId == conversationId && r.Turn == turn, cancellationToken)
            .ConfigureAwait(false);

        if (rating is not { } value)
        {
            if (existing is not null)
            {
                context.AssistantRatings.Remove(existing);
            }

            return RateResult.Rated;
        }

        if (existing is null)
        {
            context.AssistantRatings.Add(new AssistantRatingRow { ConversationId = conversationId, Turn = turn, Rating = value, RatedAt = at });
        }
        else
        {
            existing.Rating = value;
            existing.RatedAt = at;
        }

        return RateResult.Rated;
    }
}
```

In `InfrastructureRegistration.cs`, beside `IAssistantHistory`:

```csharp
        services.AddScoped<IAssistantRatings, AssistantRatings>();
        services.AddScoped<IAssistantOutcomeLog, AssistantOutcomeLog>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~Assistant|FullyQualifiedName~RegistrationCompleteness" --nologo` (Docker must be running).
Expected: PASS, the new tests and every existing assistant test (the migration applies in `PostgresFixture`).

- [ ] **Step 7: Commit**

```bash
git add src/Application/Assistant/AssistantFeedback.cs src/Infrastructure tests/Infrastructure.Tests/Assistant
git commit -m "feat(assistant): keep ratings and outcomes beside the transcript, deleted with it"
```

---

## Task 4: Every answered question leaves an outcome

**Files:**
- Modify: `src/Application/Assistant/Quick/AskQuick.cs`, `src/Application/Assistant/AskAssistant.cs`
- Test: `tests/Application.Tests/Assistant/Quick/AskQuickHandlerTests.cs`, `tests/Application.Tests/Assistant/AskAssistantHandlerTests.cs`

**Interfaces:**
- Consumes: Task 1's `RouteDecision.Evidence`; Task 2's turn arithmetic; Task 3's `IAssistantOutcomeLog`, `AnsweredQuestion`, `AnswerPath`.
- Produces: both handlers take an `IAssistantOutcomeLog` (last constructor parameter before `ICurrentUser`), and record exactly one `AnsweredQuestion` per saved question, after `Append`, never on a failure.

- [ ] **Step 1: Write the failing tests**

In `AskQuickHandlerTests.cs`: add a field `private readonly IAssistantOutcomeLog _outcomes = Substitute.For<IAssistantOutcomeLog>();`, pass `_outcomes` to the handler (and to the second handler the not-ready test builds), then add:

```csharp
    [Fact]
    public async Task AnInstantAnswer_IsLoggedWithItsIntentAndScore_AndNoQuestionText()
    {
        _embedder.Add("show my orders", 1, 0.05f);

        var result = await AskAsync("show my orders");

        _outcomes.Received(1).Record(Arg.Is<AnsweredQuestion>(o =>
            o.ConversationId == result.Value.ConversationId
            && o.QuestionTurn == 0
            && o.AnswerTurn == 1
            && o.At == Now
            && o.Path == AnswerPath.Instant
            && o.TargetId == QuickIntents.RecentOrders
            && o.BestScore > 0.99
            && o.Margin > 0.9
            && !o.Corrected
            && o.Scope == ToolScope.Member));
    }

    [Fact]
    public async Task Suggestions_AreLoggedAsSuggested()
    {
        _embedder.Add("orders or export", 1, 1);

        await AskAsync("orders or export");

        _outcomes.Received(1).Record(Arg.Is<AnsweredQuestion>(o => o.Path == AnswerPath.Suggested && o.TargetId == null));
    }
```

and in `WhenTheIndexIsNotReady_TheQuestionFailsUnavailable_AndNothingIsWrittenOrStored` add `_outcomes.DidNotReceiveWithAnyArgs().Record(default!);`.

In `AskAssistantHandlerTests.cs`: add the same field, pass it to `Handler`, then add:

```csharp
    [Fact]
    public async Task HandleAsync_LogsTheAnswerAsAnAiAnswer()
    {
        ModelReturns(ToolUse(), Answer());

        var result = await AskAsync(scope: ToolScope.Member);

        _outcomes.Received(1).Record(Arg.Is<AnsweredQuestion>(o =>
            o.ConversationId == result.Value.ConversationId
            && o.QuestionTurn == 0
            && o.AnswerTurn == 3
            && o.Path == AnswerPath.Ai
            && o.TargetId == null
            && o.BestScore == null
            && o.Scope == ToolScope.Member));
    }

    [Fact]
    public async Task HandleAsync_AFailedQuestion_LogsNothing()
    {
        _model.NextTurnAsync(Arg.Any<AssistantRequest>(), _output, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AssistantModelTurn>(new Error(ErrorKind.Unavailable, "assistant.unavailable", "Down.")));

        await AskAsync();

        _outcomes.DidNotReceiveWithAnyArgs().Record(default!);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build tests/Application.Tests --nologo -v q`
Expected: FAIL — neither handler takes an `IAssistantOutcomeLog`.

- [ ] **Step 3: Record the outcome**

`AskQuickHandler`: add `IAssistantOutcomeLog outcomes` to the primary constructor (before `ICurrentUser currentUser`). After the `conversations.Append(...)` line:

```csharp
        outcomes.Record(new AnsweredQuestion(
            opened.Value.Id,
            QuestionTurn: opened.Value.Turns.Count,
            AnswerTurn: answerTurn,
            At: now,
            Path: PathOf(reply),
            TargetId: TargetOf(routed.Value),
            BestScore: routed.Value.Evidence.BestScore,
            Margin: routed.Value.Evidence.Margin,
            Corrected: routed.Value.Evidence.Corrected,
            Scope: command.Scope));
```

and add to the class:

```csharp
    // What the user got decides the path: an intent whose composer asks "which product?" is a
    // suggestion, whatever the router chose.
    private static AnswerPath PathOf(QuickReply reply) =>
        reply switch
        {
            QuickAnswer { Kind: QuickAnswerKind.Help } => AnswerPath.Help,
            QuickAnswer => AnswerPath.Instant,
            _ => AnswerPath.Suggested,
        };

    private static string? TargetOf(RouteDecision decision) =>
        decision switch
        {
            RouteToIntent intent => intent.Intent.Id,
            RouteToHelp help => help.Section.ArticleId,
            _ => null,
        };
```

`AskAssistantHandler`: add `IAssistantOutcomeLog outcomes` to the primary constructor (before `ICurrentUser currentUser`). After `var answerTurn = …`:

```csharp
        outcomes.Record(new AnsweredQuestion(
            opened.Value.Id,
            QuestionTurn: opened.Value.Turns.Count,
            AnswerTurn: answerTurn,
            At: now,
            Path: AnswerPath.Ai,
            TargetId: null,
            BestScore: null,
            Margin: null,
            Corrected: false,
            Scope: command.Scope));
```

DI resolves the new parameter from Task 3's registration in both hosts (the worker registers both handlers too; `NoAssistantOutput` aside, nothing there is per host).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Application.Tests --nologo`, then `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~RegistrationCompleteness" --nologo`, then `dotnet test tests/Worker.IntegrationTests --nologo` (the worker's container validates both handlers' graphs at build).
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Application/Assistant tests/Application.Tests/Assistant
git commit -m "feat(assistant): log how each saved question was answered"
```

---

## Task 5: Rating an answer

**Files:**
- Create: `src/Application/Assistant/RateAssistantAnswer.cs`
- Modify: `src/Infrastructure/InfrastructureRegistration.cs`, `src/Api/Assistant/AssistantConversationsController.cs`, `src/Api/Assistant/AssistantDtos.cs`
- Regenerate: the contract
- Test: `tests/Application.Tests/Assistant/RateAssistantAnswerHandlerTests.cs`, `tests/Api.IntegrationTests/Assistant/AssistantRatingEndpointTests.cs`

**Interfaces:**
- Consumes: Task 3's `IAssistantRatings`, `RateResult`, `AnswerRating`; Task 2's `turn` on the quick endpoint's `done` event.
- Produces: `record RateAssistantAnswer(Guid ConversationId, int Turn, AnswerRating? Rating) : ICommand<bool>` with `RateAssistantAnswerValidator`; `PUT /api/assistant/conversations/{id}/turns/{turn}/rating` with body `RateAnswerRequest { AnswerRating? Rating }` → 204, 400 (`assistant.not_an_answer` or validation), 404.

- [ ] **Step 1: Write the failing tests**

`tests/Application.Tests/Assistant/RateAssistantAnswerHandlerTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant;

public sealed class RateAssistantAnswerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Caller = Guid.NewGuid();
    private readonly IAssistantRatings _ratings = Substitute.For<IAssistantRatings>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public RateAssistantAnswerHandlerTests()
    {
        _user.Id.Returns(Caller);
        _clock.UtcNow.Returns(Now);
    }

    private Task<Result<bool>> RateAsync(RateResult outcome, AnswerRating? rating = AnswerRating.Up)
    {
        var id = Guid.NewGuid();
        _ratings.RateAsync(id, Caller, 3, rating, Now, Arg.Any<CancellationToken>()).Returns(outcome);
        return new RateAssistantAnswerHandler(_ratings, _user, _clock)
            .HandleAsync(new RateAssistantAnswer(id, 3, rating), CancellationToken.None);
    }

    [Fact]
    public async Task HandleAsync_Rated_Succeeds()
    {
        (await RateAsync(RateResult.Rated)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_NullClears()
    {
        (await RateAsync(RateResult.Rated, rating: null)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_NotFound_IsTheSameNotFoundAsAMissingConversation()
    {
        var result = await RateAsync(RateResult.NotFound);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
        result.Error.Code.Should().Be("assistant.conversation_not_found");
    }

    [Fact]
    public async Task HandleAsync_NotAnAnswer_IsAValidationFailure()
    {
        var result = await RateAsync(RateResult.NotAnAnswer);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("assistant.not_an_answer");
    }

    [Fact]
    public async Task HandleAsync_WithoutACaller_IsUnauthorized()
    {
        _user.Id.Returns((Guid?)null);

        var result = await new RateAssistantAnswerHandler(_ratings, _user, _clock)
            .HandleAsync(new RateAssistantAnswer(Guid.NewGuid(), 3, AnswerRating.Up), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }

    [Theory]
    [InlineData(-1)]
    public void Validator_ANegativeTurn_IsInvalid(int turn)
    {
        new RateAssistantAnswerValidator().Validate(new RateAssistantAnswer(Guid.NewGuid(), turn, AnswerRating.Up)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_AnUndefinedRating_IsInvalid()
    {
        new RateAssistantAnswerValidator().Validate(new RateAssistantAnswer(Guid.NewGuid(), 1, (AnswerRating)7)).IsValid.Should().BeFalse();
    }
}
```

`tests/Api.IntegrationTests/Assistant/AssistantRatingEndpointTests.cs` (instant answers on, the AI off, as `AssistantQuickEndpointTests`):

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Assistant;

[Collection(nameof(ApiFactoryCollection))]
public sealed class AssistantRatingEndpointTests(ApiFactory factory) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _quick = null!;

    public Task InitializeAsync()
    {
        _quick = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Assistant:Quick:Enabled", "true");
            b.UseSetting("RateLimiting:AssistantQuick:PermitLimit", "1000");
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _quick.DisposeAsync();

    private async Task<HttpClient> MemberAsync()
    {
        var client = _quick.CreateClient();
        (await client.PostAsJsonAsync(
            "/api/auth/register",
            new { Username = $"m{Guid.NewGuid():N}"[..32], Password = ApiFactory.RegisteredPassword, DisplayName = "Member" })).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Asks one quick question and returns the done event: its conversation and the answer's turn.</summary>
    private static async Task<(Guid Conversation, int Turn)> AskAsync(HttpClient client)
    {
        var body = await (await client.PostAsJsonAsync("/api/assistant/quick", new { Question = "Has my last order shipped?" })).Content.ReadAsStringAsync();
        var done = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => JsonDocument.Parse(block.Split('\n').Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..]).RootElement)
            .Single(e => e.GetProperty("type").GetString() == "done");
        return (done.GetProperty("conversationId").GetGuid(), done.GetProperty("turn").GetInt32());
    }

    private static string RatingPath(Guid conversation, int turn) => $"/api/assistant/conversations/{conversation}/turns/{turn}/rating";

    [Fact]
    public async Task Rate_TheAnswer_IsNoContent()
    {
        var client = await MemberAsync();
        var (conversation, turn) = await AskAsync(client);

        var response = await client.PutAsJsonAsync(RatingPath(conversation, turn), new { Rating = "Down" });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Rate_TheQuestionTurn_IsBadRequest()
    {
        var client = await MemberAsync();
        var (conversation, turn) = await AskAsync(client);

        var response = await client.PutAsJsonAsync(RatingPath(conversation, turn - 1), new { Rating = "Up" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString().Should().Be("assistant.not_an_answer");
    }

    [Fact]
    public async Task Rate_AnotherUsersAnswer_IsNotFound()
    {
        var (conversation, turn) = await AskAsync(await MemberAsync());
        var stranger = await MemberAsync();

        var response = await stranger.PutAsJsonAsync(RatingPath(conversation, turn), new { Rating = "Up" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rate_Anonymously_IsUnauthorized()
    {
        var response = await _quick.CreateClient().PutAsJsonAsync(RatingPath(Guid.NewGuid(), 1), new { Rating = "Up" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build --nologo -v q`
Expected: FAIL — `RateAssistantAnswer`, its handler and validator do not exist.

- [ ] **Step 3: The command**

`src/Application/Assistant/RateAssistantAnswer.cs`:

```csharp
using AiFramework.Application.Abstractions;
using FluentValidation;

namespace AiFramework.Application.Assistant;

/// <summary>The owner rates one answer, or clears its rating with a null.</summary>
public sealed record RateAssistantAnswer(Guid ConversationId, int Turn, AnswerRating? Rating) : ICommand<bool>;

public sealed class RateAssistantAnswerValidator : AbstractValidator<RateAssistantAnswer>
{
    public RateAssistantAnswerValidator()
    {
        RuleFor(c => c.Turn).GreaterThanOrEqualTo(0);
        RuleFor(c => c.Rating).IsInEnum().When(c => c.Rating is not null);
    }
}

public sealed class RateAssistantAnswerHandler(IAssistantRatings ratings, ICurrentUser currentUser, IClock clock)
    : ICommandHandler<RateAssistantAnswer, bool>
{
    public async Task<Result<bool>> HandleAsync(RateAssistantAnswer command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } ownerId)
        {
            return Result.Failure<bool>(new Error(ErrorKind.Unauthorized, "assistant.no_caller", "Sign in to use the assistant."));
        }

        var rated = await ratings.RateAsync(command.ConversationId, ownerId, command.Turn, command.Rating, clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return rated switch
        {
            RateResult.Rated => Result.Success(true),
            RateResult.NotAnAnswer => Result.Failure<bool>(new Error(
                ErrorKind.Validation, "assistant.not_an_answer", "Only an answer can be rated.")),
            _ => Result.Failure<bool>(new Error(
                ErrorKind.NotFound, "assistant.conversation_not_found", "That conversation does not exist or has expired.")),
        };
    }
}
```

Register in `RegisterAssistant`, after the delete command:

```csharp
        services.AddCommand<RateAssistantAnswer, bool, RateAssistantAnswerHandler>();
        services.AddScoped<IValidator<RateAssistantAnswer>, RateAssistantAnswerValidator>();
```

- [ ] **Step 4: The endpoint**

`AssistantDtos.cs`, after `RenameAssistantConversationRequest`:

```csharp
/// <summary>A rating for one answer: Up, Down, or null to clear it.</summary>
public sealed record RateAnswerRequest
{
    public AnswerRating? Rating { get; init; }
}
```

`AssistantConversationsController.cs`, after `Rename`:

```csharp
    /// <summary>The owner rates one answer (<paramref name="turn"/> is the turn the answer's events and messages carry), or clears it with a null.</summary>
    [HttpPut("{id:guid}/turns/{turn:int}/rating")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rate(Guid id, int turn, RateAnswerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!availability.AnyEnabled)
        {
            return Disabled();
        }

        var result = await commands.SendAsync(new RateAssistantAnswer(id, turn, request.Rating), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NoContent() : result.Problem(HttpContext);
    }
```

Regenerate the contract as in Task 2 Step 4. Expected: the new path, `RateAnswerRequest` and the `AnswerRating` enum.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~RateAssistantAnswer" --nologo`, `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~RegistrationCompleteness" --nologo`, `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~Assistant|FullyQualifiedName~ArchitectureTests" --nologo`.
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src tests openapi frontend/src/api/schema.d.ts
git commit -m "feat(assistant): let the owner rate an answer"
```

---

## Task 6: A reopened conversation knows each answer's turn and rating

**Files:**
- Modify: `src/Application/Assistant/AssistantHistory.cs` (`AssistantDisplayMessage`), `src/Infrastructure/Assistant/AssistantDisplay.cs`, `src/Infrastructure/Assistant/AssistantHistoryStore.cs` (`ReadAsync`), `src/Api/Assistant/AssistantDtos.cs` (`AssistantMessageResponse`), `src/Api/Assistant/AssistantConversationsController.cs` (`Get`)
- Regenerate: the contract
- Test: `tests/Infrastructure.Tests/Assistant/AssistantDisplayTests.cs`, `tests/Infrastructure.Tests/Assistant/AssistantHistoryStoreTests.cs`

**Interfaces:**
- Consumes: Task 3's `AssistantRatings` rows and `AnswerRating`.
- Produces: `AssistantDisplayMessage(string Role, string Text, IReadOnlyList<string> Tools, QuickAnswer? Answer = null, QuickSuggestions? Suggestions = null, int? Turn = null, AnswerRating? Rating = null)`; `AssistantDisplay.Build(IEnumerable<AssistantTurnRow> turns, IReadOnlyDictionary<int, AnswerRating>? ratings = null)`; `AssistantMessageResponse.Turn` (`int?`) and `.Rating` (`AnswerRating?`). A user message's `Turn` and `Rating` are null.

- [ ] **Step 1: Write the failing tests**

In `AssistantDisplayTests.cs` add (use the file's existing helpers for building `AssistantTurnRow`s from turns; if there is none, encode with `AssistantTurnCodec.Encode(conversationId, seq, turn, at)`):

```csharp
    [Fact]
    public void Build_AnAiAnswerAfterAToolRound_CarriesItsFinalModelTurnAndRating()
    {
        var id = Guid.NewGuid();
        var rows = new[]
        {
            AssistantTurnCodec.Encode(id, 0, new UserQuestionTurn("Did jobs fail?", Now), Now),
            AssistantTurnCodec.Encode(id, 1, new ModelTurn("""[{"type":"tool_use","id":"toolu_1","name":"job_health","input":{}}]"""), Now),
            AssistantTurnCodec.Encode(id, 2, new ToolResultsTurn([new AssistantToolResult("toolu_1", "{}", false)]), Now),
            AssistantTurnCodec.Encode(id, 3, new ModelTurn("""[{"type":"text","text":"Three failed."}]"""), Now),
        };

        var messages = AssistantDisplay.Build(rows, new Dictionary<int, AnswerRating> { [3] = AnswerRating.Down });

        messages[0].Turn.Should().BeNull();
        messages[1].Turn.Should().Be(3);
        messages[1].Rating.Should().Be(AnswerRating.Down);
    }

    [Fact]
    public void Build_AQuickAnswer_CarriesItsOwnTurn_AndNoRatingWhenUnrated()
    {
        var id = Guid.NewGuid();
        var answer = new QuickAnswer(QuickAnswerKind.Instant, null, "Shipped.", [], null, null, null);
        var rows = new[]
        {
            AssistantTurnCodec.Encode(id, 0, new UserQuestionTurn("Has it shipped?", Now), Now),
            AssistantTurnCodec.Encode(id, 1, new QuickAnswerTurn(answer, "Shipped."), Now),
        };

        var messages = AssistantDisplay.Build(rows);

        messages[1].Turn.Should().Be(1);
        messages[1].Rating.Should().BeNull();
    }
```

(`Now` is the file's existing timestamp constant; add one if it has none. Build `QuickAnswer` with the constructor the code has, as in Task 2.)

In `AssistantHistoryStoreTests.cs` add:

```csharp
    [Fact]
    public async Task ReadAsync_ReportsTheOwnersRatingOnTheAnswer()
    {
        var owner = Guid.NewGuid();
        var id = await SeedAsync(owner, "Did jobs fail?", Now, ToolRound);
        await using (var context = fixture.CreateContext())
        {
            await new AssistantRatings(context).RateAsync(id, owner, 3, AnswerRating.Up, Now, CancellationToken.None);
            await context.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var view = await new AssistantHistoryStore(read).ReadAsync(id, owner, CancellationToken.None);

        view!.Messages[1].Turn.Should().Be(3);
        view.Messages[1].Rating.Should().Be(AnswerRating.Up);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build tests/Infrastructure.Tests --nologo -v q`
Expected: FAIL — `AssistantDisplayMessage` has no `Turn` or `Rating`, and `Build` takes no ratings.

- [ ] **Step 3: Carry the turn and the rating**

`AssistantHistory.cs`, replace the `AssistantDisplayMessage` record header with:

```csharp
public sealed record AssistantDisplayMessage(
    string Role,
    string Text,
    IReadOnlyList<string> Tools,
    QuickAnswer? Answer = null,
    QuickSuggestions? Suggestions = null,
    int? Turn = null,
    AnswerRating? Rating = null)
```

and add to its summary: "An answer carries the turn a rating names and the owner's rating, if any."

`AssistantDisplay.cs`:
- Change `Build`'s signature to `Build(IEnumerable<AssistantTurnRow> turns, IReadOnlyDictionary<int, AnswerRating>? ratings = null)`, with `ratings ??= new Dictionary<int, AnswerRating>();` as its first line.
- Add a local `int? lastModel = null;` beside `text` and `tools`; in the `Model` case set `lastModel = turn.Seq;`.
- Give `Flush` two more parameters, `int? turn` and `IReadOnlyDictionary<int, AnswerRating> ratings`, pass `lastModel` and `ratings` at every call, and build its message as:

```csharp
        messages.Add(new AssistantDisplayMessage(
            AssistantDisplayMessage.AssistantRole,
            text.ToString(),
            [.. tools.Select(static t => t.Name)],
            Turn: turn,
            Rating: turn is { } seq && ratings.TryGetValue(seq, out var rating) ? rating : null));
```

- Give `AddQuick` the `ratings` parameter too, and build its two messages with `Turn: turn.Seq, Rating: ratings.TryGetValue(turn.Seq, out var rating) ? rating : null`.

`AssistantHistoryStore.ReadAsync`, before the `return`:

```csharp
        var ratings = await context.AssistantRatings
            .AsNoTracking()
            .Where(r => r.ConversationId == id)
            .ToDictionaryAsync(r => r.Turn, r => r.Rating, cancellationToken)
            .ConfigureAwait(false);

        return new AssistantConversationView(row.Id, row.Title, AssistantDisplay.Build(turns, ratings));
```

`AssistantDtos.cs`, in `AssistantMessageResponse`:

```csharp
    /// <summary>The turn an answer is rated on; null on a question.</summary>
    public int? Turn { get; init; }

    /// <summary>The owner's rating of this answer, if any.</summary>
    public AnswerRating? Rating { get; init; }
```

`AssistantConversationsController.Get`, in the message projection add `Turn = m.Turn, Rating = m.Rating,`.

Regenerate the contract as in Task 2 Step 4.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~Assistant" --nologo`, `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~Assistant" --nologo`.
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests openapi frontend/src/api/schema.d.ts
git commit -m "feat(assistant): show each saved answer's turn and its rating"
```

---

## Task 7: The insights query and endpoint

**Files:**
- Create: `src/Application/Assistant/AssistantInsights.cs`, `src/Infrastructure/Assistant/AssistantInsightsReader.cs`, `src/Api/Monitoring/MonitoringAssistantController.cs`
- Modify: `src/Infrastructure/InfrastructureRegistration.cs`, `src/Api/Monitoring/MonitoringDtos.cs`
- Regenerate: the contract
- Test: `tests/Application.Tests/Assistant/GetAssistantInsightsHandlerTests.cs`, `tests/Infrastructure.Tests/Assistant/AssistantInsightsReaderTests.cs`, `tests/Api.IntegrationTests/Monitoring/MonitoringAssistantTests.cs`

**Interfaces:**
- Consumes: Task 3's rows; `OperationalText.Scrub`.
- Produces (Application):
  - `record InsightsDay(DateOnly Day, int Instant, int Help, int Suggested, int Ai)`
  - `record InsightsTarget(AnswerPath Path, string TargetId, int Questions, int Up, int Down)`
  - `record InsightsMiss(DateTimeOffset At, AnswerPath Path, string? TargetId, double? BestScore, AnswerRating? Rating, string Question)`
  - `record AssistantInsightsView(DateTimeOffset Since, IReadOnlyList<InsightsDay> Days, IReadOnlyList<InsightsTarget> Targets, IReadOnlyList<InsightsMiss> Misses)`
  - `interface IAssistantInsightsReader { Task<IReadOnlyList<InsightsDay>> DaysAsync(DateTimeOffset since, CancellationToken ct); Task<IReadOnlyList<InsightsTarget>> TargetsAsync(DateTimeOffset since, int limit, CancellationToken ct); Task<IReadOnlyList<InsightsMiss>> MissesAsync(DateTimeOffset since, int limit, CancellationToken ct); }` — `MissesAsync` returns the question text **unscrubbed**; the handler scrubs it.
  - `record GetAssistantInsights(int Days) : IQuery<AssistantInsightsView>`; `GetAssistantInsightsHandler.MaxDays = 30`, `.MaxTargets = 20`, `.MaxMisses = 50`.
- Produces (Api): `GET /api/monitoring/assistant/insights?days=7` → `AssistantInsightsResponse`, administrators only.

- [ ] **Step 1: Write the failing tests**

`tests/Application.Tests/Assistant/GetAssistantInsightsHandlerTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Assistant;

public sealed class GetAssistantInsightsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    private readonly IAssistantInsightsReader _reader = Substitute.For<IAssistantInsightsReader>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public GetAssistantInsightsHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _reader.DaysAsync(default, default).ReturnsForAnyArgs([]);
        _reader.TargetsAsync(default, default, default).ReturnsForAnyArgs([]);
        _reader.MissesAsync(default, default, default).ReturnsForAnyArgs([]);
    }

    private Task<Result<AssistantInsightsView>> GetAsync(int days) =>
        new GetAssistantInsightsHandler(_reader, _clock).HandleAsync(new GetAssistantInsights(days), CancellationToken.None);

    [Fact]
    public async Task HandleAsync_ReadsFromTheStartOfTheWindow()
    {
        var result = await GetAsync(7);

        result.Value.Since.Should().Be(Now.AddDays(-7));
        await _reader.Received(1).MissesAsync(Now.AddDays(-7), GetAssistantInsightsHandler.MaxMisses, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ScrubsEveryMissedQuestion()
    {
        _reader.MissesAsync(default, default, default).ReturnsForAnyArgs(
            [new InsightsMiss(Now, AnswerPath.Ai, null, null, null, "Why can't jane@example.com sign in from 10.1.2.3?")]);

        var result = await GetAsync(7);

        var question = result.Value.Misses.Single().Question;
        question.Should().NotContain("jane@example.com").And.NotContain("10.1.2.3");
        question.Should().Contain("[email]");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task HandleAsync_AWindowOutsideOneTo30Days_IsInvalid(int days)
    {
        var result = await GetAsync(days);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("assistant.insights_window");
    }
}
```

(Check `OperationalText.Scrub`'s replacement tokens in `src/Application/Assistant/OperationalText.cs` — `[email]` is the one the existing toolbox tests assert; keep the IP assertion as `NotContain` only.)

`tests/Infrastructure.Tests/Assistant/AssistantInsightsReaderTests.cs`:

```csharp
using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Assistant;
using AiFramework.Infrastructure.Tests.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Assistant;

/// <summary>
/// The database is shared by every test in the collection, so each test dates its rows in a year
/// of its own and reads only that window.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AssistantInsightsReaderTests(PostgresFixture fixture)
{
    private async Task<Guid> SeedAsync(DateTimeOffset at, string question, AnswerPath path, string? target, AnswerRating? rating = null)
    {
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        var conversations = new AssistantConversations(context);
        conversations.Start(id, owner, question, at);
        conversations.Append(id, [new UserQuestionTurn(question, at), new ModelTurn("""[{"type":"text","text":"An answer."}]""")], at);
        new AssistantOutcomeLog(context).Record(new AnsweredQuestion(id, 0, 1, at, path, target, 0.9, 0.1, false, ToolScope.Member));
        await context.SaveChangesAsync();
        if (rating is { } value)
        {
            await new AssistantRatings(context).RateAsync(id, owner, 1, value, at, CancellationToken.None);
            await context.SaveChangesAsync();
        }

        return id;
    }

    [Fact]
    public async Task DaysAsync_CountsEachPathPerUtcDay()
    {
        var day = new DateTimeOffset(2031, 3, 4, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(day, "a", AnswerPath.Instant, "order_status");
        await SeedAsync(day.AddHours(1), "b", AnswerPath.Instant, "order_status");
        await SeedAsync(day.AddHours(2), "c", AnswerPath.Ai, null);
        await SeedAsync(day.AddDays(1), "d", AnswerPath.Suggested, null);

        await using var context = fixture.CreateContext();
        var days = await new AssistantInsightsReader(context).DaysAsync(day.AddHours(-10), CancellationToken.None);

        days.Where(d => d.Day.Year == 2031).Should().Equal(
            new InsightsDay(new DateOnly(2031, 3, 4), 2, 0, 0, 1),
            new InsightsDay(new DateOnly(2031, 3, 5), 0, 0, 1, 0));
    }

    [Fact]
    public async Task MissesAsync_ReturnsSuggestedAiAndThumbsDown_NewestFirst_WithTheQuestionText()
    {
        var at = new DateTimeOffset(2032, 5, 6, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(at, "answered fine", AnswerPath.Instant, "order_status");
        await SeedAsync(at.AddMinutes(1), "wanted a poem", AnswerPath.Suggested, null);
        await SeedAsync(at.AddMinutes(2), "asked the AI", AnswerPath.Ai, null);
        await SeedAsync(at.AddMinutes(3), "wrong instant answer", AnswerPath.Instant, "order_status", AnswerRating.Down);

        await using var context = fixture.CreateContext();
        var misses = await new AssistantInsightsReader(context).MissesAsync(at.AddMinutes(-1), 50, CancellationToken.None);

        misses.Where(m => m.At.Year == 2032).Select(m => m.Question).Should().Equal("wrong instant answer", "asked the AI", "wanted a poem");
        misses.Single(m => m.Question == "wrong instant answer").Rating.Should().Be(AnswerRating.Down);
    }

    [Fact]
    public async Task MissesAsync_ADeletedConversation_IsGone()
    {
        var at = new DateTimeOffset(2033, 7, 8, 10, 0, 0, TimeSpan.Zero);
        var id = await SeedAsync(at, "soon deleted", AnswerPath.Ai, null);
        await using (var context = fixture.CreateContext())
        {
            context.AssistantConversations.Remove(context.AssistantConversations.Single(c => c.Id == id));
            await context.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        (await new AssistantInsightsReader(read).MissesAsync(at.AddMinutes(-1), 50, CancellationToken.None))
            .Should().NotContain(m => m.Question == "soon deleted");
    }

    [Fact]
    public async Task TargetsAsync_CountsQuestionsAndRatingsPerTarget()
    {
        var at = new DateTimeOffset(2034, 9, 10, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(at, "x", AnswerPath.Instant, "export_status", AnswerRating.Up);
        await SeedAsync(at.AddMinutes(1), "y", AnswerPath.Instant, "export_status", AnswerRating.Down);
        await SeedAsync(at.AddMinutes(2), "z", AnswerPath.Instant, "export_status");

        await using var context = fixture.CreateContext();
        var targets = await new AssistantInsightsReader(context).TargetsAsync(at.AddMinutes(-1), 20, CancellationToken.None);

        targets.Should().Contain(new InsightsTarget(AnswerPath.Instant, "export_status", 3, 1, 1));
    }
}
```

`tests/Api.IntegrationTests/Monitoring/MonitoringAssistantTests.cs` (use the factory's existing `CreateAdminClientAsync` and `CreateAuthenticatedClientAsync` helpers; check their names in `ApiFactory.cs`):

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Monitoring;

[Collection(nameof(ApiFactoryCollection))]
public sealed class MonitoringAssistantTests(ApiFactory factory)
{
    [Fact]
    public async Task Insights_ForAnAdministrator_IsOk()
    {
        var admin = await factory.CreateAdminClientAsync();

        var insights = await admin.GetFromJsonAsync<JsonElement>("/api/monitoring/assistant/insights?days=7");

        insights.GetProperty("days").ValueKind.Should().Be(JsonValueKind.Array);
        insights.GetProperty("misses").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Insights_ForAMember_IsForbidden()
    {
        var member = await factory.CreateAuthenticatedClientAsync();

        (await member.GetAsync("/api/monitoring/assistant/insights?days=7")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Insights_AWindowOver30Days_IsBadRequest()
    {
        var admin = await factory.CreateAdminClientAsync();

        (await admin.GetAsync("/api/monitoring/assistant/insights?days=31")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build --nologo -v q`
Expected: FAIL — the insights types, reader and handler do not exist.

- [ ] **Step 3: The query**

`src/Application/Assistant/AssistantInsights.cs`:

```csharp
using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Assistant;

public sealed record InsightsDay(DateOnly Day, int Instant, int Help, int Suggested, int Ai);

public sealed record InsightsTarget(AnswerPath Path, string TargetId, int Questions, int Up, int Down);

/// <summary>A question the assistant missed: it ended in suggestions or the AI, or was rated 👎.</summary>
public sealed record InsightsMiss(DateTimeOffset At, AnswerPath Path, string? TargetId, double? BestScore, AnswerRating? Rating, string Question);

public sealed record AssistantInsightsView(
    DateTimeOffset Since, IReadOnlyList<InsightsDay> Days, IReadOnlyList<InsightsTarget> Targets, IReadOnlyList<InsightsMiss> Misses);

/// <summary>
/// Reads the outcome log. Misses carry the question text read live from the transcript, so a
/// deleted or expired conversation's question is gone with it; nothing here stores a copy.
/// </summary>
public interface IAssistantInsightsReader
{
    public Task<IReadOnlyList<InsightsDay>> DaysAsync(DateTimeOffset since, CancellationToken cancellationToken);

    public Task<IReadOnlyList<InsightsTarget>> TargetsAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken);

    /// <summary>The question text is returned as stored; the caller scrubs it.</summary>
    public Task<IReadOnlyList<InsightsMiss>> MissesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken);
}

/// <summary>For administrators; the API gates it with Monitoring.Read. ADR 0037.</summary>
public sealed record GetAssistantInsights(int Days) : IQuery<AssistantInsightsView>;

public sealed class GetAssistantInsightsHandler(IAssistantInsightsReader reader, IClock clock)
    : IQueryHandler<GetAssistantInsights, AssistantInsightsView>
{
    /// <summary>Conversations, and with them their outcomes, are kept 30 days.</summary>
    public const int MaxDays = 30;

    public const int MaxTargets = 20;

    public const int MaxMisses = 50;

    public async Task<Result<AssistantInsightsView>> HandleAsync(GetAssistantInsights query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Days is < 1 or > MaxDays)
        {
            return Result.Failure<AssistantInsightsView>(new Error(
                ErrorKind.Validation, "assistant.insights_window", $"The window is 1 to {MaxDays} days."));
        }

        var since = clock.UtcNow.AddDays(-query.Days);
        var days = await reader.DaysAsync(since, cancellationToken).ConfigureAwait(false);
        var targets = await reader.TargetsAsync(since, MaxTargets, cancellationToken).ConfigureAwait(false);
        var misses = await reader.MissesAsync(since, MaxMisses, cancellationToken).ConfigureAwait(false);

        // A member's own words, read by an administrator: never shown unscrubbed (ADR 0037).
        return Result.Success(new AssistantInsightsView(
            since,
            days,
            targets,
            [.. misses.Select(static m => m with { Question = OperationalText.Scrub(m.Question) ?? string.Empty })]));
    }
}
```

Register in `RegisterAssistant`:

```csharp
        services.AddQuery<GetAssistantInsights, AssistantInsightsView, GetAssistantInsightsHandler>();
```

and beside the other assistant ports: `services.AddScoped<IAssistantInsightsReader, AssistantInsightsReader>();`.

- [ ] **Step 4: The reader**

`src/Infrastructure/Assistant/AssistantInsightsReader.cs`. Days and targets are aggregated in SQL (one row per day and path, one per target); misses are the newest outcomes that missed, then their questions' turns are read in one query and decoded with the codec. Column names follow the repo's EF convention (PascalCase, quoted), table names are the configured snake_case ones:

```csharp
using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Assistant;

internal sealed class AssistantInsightsReader(AiFrameworkDbContext context) : IAssistantInsightsReader
{
    public async Task<IReadOnlyList<InsightsDay>> DaysAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var rows = await context.Database
            .SqlQuery<DayPathCount>($"""
                SELECT (o."At" AT TIME ZONE 'UTC')::date AS "Day", o."Path" AS "Path", count(*)::int AS "Count"
                FROM assistant_outcomes o
                WHERE o."At" >= {since}
                GROUP BY 1, 2
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows
            .GroupBy(static r => r.Day)
            .OrderBy(static g => g.Key)
            .Select(static g => new InsightsDay(
                g.Key,
                Count(g, AnswerPath.Instant),
                Count(g, AnswerPath.Help),
                Count(g, AnswerPath.Suggested),
                Count(g, AnswerPath.Ai)))];
    }

    public async Task<IReadOnlyList<InsightsTarget>> TargetsAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken)
    {
        var rows = await context.Database
            .SqlQuery<TargetCount>($"""
                SELECT o."Path" AS "Path", o."TargetId" AS "TargetId", count(*)::int AS "Questions",
                       count(*) FILTER (WHERE r."Rating" = 'Up')::int AS "Up",
                       count(*) FILTER (WHERE r."Rating" = 'Down')::int AS "Down"
                FROM assistant_outcomes o
                LEFT JOIN assistant_ratings r ON r."ConversationId" = o."ConversationId" AND r."Turn" = o."AnswerTurn"
                WHERE o."At" >= {since} AND o."TargetId" IS NOT NULL
                GROUP BY o."Path", o."TargetId"
                ORDER BY count(*) DESC, o."TargetId"
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(static r => new InsightsTarget(Enum.Parse<AnswerPath>(r.Path), r.TargetId, r.Questions, r.Up, r.Down))];
    }

    public async Task<IReadOnlyList<InsightsMiss>> MissesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken)
    {
        var missed = await (
                from o in context.AssistantOutcomes.AsNoTracking()
                join r in context.AssistantRatings.AsNoTracking()
                    on new { o.ConversationId, Turn = o.AnswerTurn } equals new { r.ConversationId, r.Turn } into rated
                from r in rated.DefaultIfEmpty()
                where o.At >= since
                    && (o.Path == AnswerPath.Suggested || o.Path == AnswerPath.Ai || (r != null && r.Rating == AnswerRating.Down))
                orderby o.At descending
                select new { o.ConversationId, o.QuestionTurn, o.At, o.Path, o.TargetId, o.BestScore, Rating = r == null ? (AnswerRating?)null : r.Rating })
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var conversationIds = missed.Select(static m => m.ConversationId).Distinct().ToList();
        var questions = await context.AssistantTurns
            .AsNoTracking()
            .Where(t => conversationIds.Contains(t.ConversationId) && t.Kind == AssistantTurnCodec.Question)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var text = questions.ToDictionary(
            static t => (t.ConversationId, t.Seq),
            static t => AssistantTurnCodec.Decode(t) is UserQuestionTurn q ? q.Text : string.Empty);

        return [.. missed.Select(m => new InsightsMiss(
            m.At, m.Path, m.TargetId, m.BestScore, m.Rating,
            text.GetValueOrDefault((m.ConversationId, m.QuestionTurn), string.Empty)))];
    }

    private static int Count(IEnumerable<DayPathCount> day, AnswerPath path) =>
        day.Where(r => r.Path == path.ToString()).Sum(static r => r.Count);

    private sealed record DayPathCount(DateOnly Day, string Path, int Count);

    private sealed record TargetCount(string Path, string TargetId, int Questions, int Up, int Down);
}
```

If EF cannot map `SqlQuery<T>` onto a private nested record (it needs an unmapped type with a parameterless shape in some versions), make the two row types `internal sealed class`es with `{ get; init; }` properties — the same adjustment `ProductRepository.FindAsync` documents — and record the deviation in the report.

- [ ] **Step 5: The endpoint**

`MonitoringDtos.cs`, append:

```csharp
public sealed record AssistantInsightsDayResponse
{
    public required DateOnly Day { get; init; }

    public required int Instant { get; init; }

    public required int Help { get; init; }

    public required int Suggested { get; init; }

    public required int Ai { get; init; }
}

public sealed record AssistantInsightsTargetResponse
{
    public required AnswerPath Path { get; init; }

    public required string TargetId { get; init; }

    public required int Questions { get; init; }

    public required int Up { get; init; }

    public required int Down { get; init; }
}

/// <summary>A missed question, scrubbed. Names no user and no conversation.</summary>
public sealed record AssistantInsightsMissResponse
{
    public required DateTimeOffset At { get; init; }

    public required AnswerPath Path { get; init; }

    public string? TargetId { get; init; }

    public double? BestScore { get; init; }

    public AnswerRating? Rating { get; init; }

    public required string Question { get; init; }
}

public sealed record AssistantInsightsResponse
{
    public required DateTimeOffset Since { get; init; }

    public required IReadOnlyList<AssistantInsightsDayResponse> Days { get; init; }

    public required IReadOnlyList<AssistantInsightsTargetResponse> Targets { get; init; }

    public required IReadOnlyList<AssistantInsightsMissResponse> Misses { get; init; }
}
```

(add `using AiFramework.Application.Assistant;` if the file lacks it).

`src/Api/Monitoring/MonitoringAssistantController.cs`:

```csharp
using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// How the assistant is doing: answers by path per day, the most-asked targets, and the questions
/// it missed, scrubbed. Administrators only (ADR 0037).
/// </summary>
[ApiController]
[Route("api/monitoring/assistant")]
[Authorize(Policy = AuthorizationPolicies.Monitoring.Read)]
public sealed class MonitoringAssistantController(IQueryDispatcher queries) : ControllerBase
{
    [HttpGet("insights")]
    [ProducesResponseType<AssistantInsightsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Insights([FromQuery] int days = 7, CancellationToken cancellationToken = default)
    {
        var result = await queries.SendAsync(new GetAssistantInsights(days), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new AssistantInsightsResponse
            {
                Since = result.Value.Since,
                Days = [.. result.Value.Days.Select(static d => new AssistantInsightsDayResponse
                {
                    Day = d.Day, Instant = d.Instant, Help = d.Help, Suggested = d.Suggested, Ai = d.Ai,
                })],
                Targets = [.. result.Value.Targets.Select(static t => new AssistantInsightsTargetResponse
                {
                    Path = t.Path, TargetId = t.TargetId, Questions = t.Questions, Up = t.Up, Down = t.Down,
                })],
                Misses = [.. result.Value.Misses.Select(static m => new AssistantInsightsMissResponse
                {
                    At = m.At, Path = m.Path, TargetId = m.TargetId, BestScore = m.BestScore, Rating = m.Rating, Question = m.Question,
                })],
            })
            : result.Problem(HttpContext);
    }
}
```

Regenerate the contract as in Task 2 Step 4.

- [ ] **Step 6: Run the tests to verify they pass**

Run, one at a time: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~Insights" --nologo`; `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~Insights|FullyQualifiedName~RegistrationCompleteness" --nologo`; `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~MonitoringAssistant|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~AuthorizationPolicy" --nologo`.
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src tests openapi frontend/src/api/schema.d.ts
git commit -m "feat(assistant): show administrators the answer rate and the missed questions"
```

---

## Task 8: Rating answers in the chat

**Files:**
- Create: `frontend/src/features/assistant/AnswerRatingButtons.tsx`, `frontend/src/features/assistant/AnswerRatingButtons.test.tsx`
- Modify: `frontend/src/features/assistant/types.ts`, `frontend/src/api/assistant.ts`, `frontend/src/features/assistant/queries.ts`, `frontend/src/features/assistant/conversation.ts`, `frontend/src/features/assistant/conversation.test.ts`, `frontend/src/features/assistant/useAssistantConversation.ts`, `frontend/src/features/assistant/Chat.tsx`, `frontend/src/features/assistant/Chat.test.tsx`, `frontend/src/features/assistant/assistant.css`

**Interfaces:**
- Consumes: the regenerated schema (`turn` on events and saved messages, `rating` on saved messages, `AnswerRating`, the rating endpoint).
- Produces: `type AnswerRating = 'Up' | 'Down'` (alias over the schema); `rateAnswer(conversationId: string, turn: number, rating: AnswerRating | null): Promise<void>`; `useRateAnswer()`; `AssistantMessage.turn` and `.rating`; the reducer action `{ type: 'rated'; turn: number; rating: AnswerRating | undefined }`; the hook's `rated(turn, rating)`; `<AnswerRatingButtons conversationId turn rating onRated />` — a `group` named "Rate this answer" with buttons "Helpful" and "Not helpful" (`aria-pressed`).

- [ ] **Step 1: Write the failing tests**

Append to `conversation.test.ts` (import what it needs from `./conversation`):

```ts
describe('turns and ratings', () => {
  const asked = conversationReducer(initialConversation, { type: 'asked', question: 'Q?', mode: 'quick' });

  it('takes the turn from the answer event', () => {
    const state = conversationReducer(asked, {
      type: 'event',
      event: { type: 'answer', turn: 1, answer: { kind: 'Instant', text: 'A.', facts: [] } },
    });

    expect(state.messages.at(-1)?.turn).toBe(1);
  });

  it('takes the turn from done for an AI answer', () => {
    const state = conversationReducer(asked, { type: 'event', event: { type: 'done', conversationId: 'c1', turn: 3 } });

    expect(state.messages.at(-1)?.turn).toBe(3);
  });

  it('records a rating on the message with that turn', () => {
    const answered = conversationReducer(asked, { type: 'event', event: { type: 'done', conversationId: 'c1', turn: 1 } });

    const rated = conversationReducer(answered, { type: 'rated', turn: 1, rating: 'Down' });

    expect(rated.messages.at(-1)?.rating).toBe('Down');
  });

  it('starts a saved conversation with its turns and ratings', () => {
    const state = fromSaved({
      id: 'c1',
      title: 'T',
      messages: [
        { role: 'user', text: 'Q?', tools: [] },
        { role: 'assistant', text: 'A.', tools: [], turn: 1, rating: 'Up' },
      ],
    });

    expect(state.messages[1]).toMatchObject({ turn: 1, rating: 'Up' });
  });
});
```

(If the generated `AssistantEventResponse`/`AssistantMessageResponse` types make a field required that these literals omit, add it with the value the reducer ignores — the test is about `turn` and `rating`.)

`frontend/src/features/assistant/AnswerRatingButtons.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { AnswerRatingButtons } from './AnswerRatingButtons';
import type { AnswerRating } from './types';

function renderButtons(rating: AnswerRating | undefined, onRated = vi.fn()): { onRated: typeof onRated } {
  render(<AnswerRatingButtons conversationId="c1" turn={3} rating={rating} onRated={onRated} />, { wrapper: withQueryClient() });
  return { onRated };
}

describe('AnswerRatingButtons', () => {
  it('sends a rating and reports it', async () => {
    let body: unknown;
    server.use(
      http.put('/api/assistant/conversations/c1/turns/3/rating', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { onRated } = renderButtons(undefined);

    await userEvent.click(screen.getByRole('button', { name: 'Not helpful' }));

    expect(body).toEqual({ rating: 'Down' });
    expect(onRated).toHaveBeenCalledWith('Down');
  });

  it('clears the rating when the chosen one is clicked again', async () => {
    let body: unknown;
    server.use(
      http.put('/api/assistant/conversations/c1/turns/3/rating', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { onRated } = renderButtons('Up');

    expect(screen.getByRole('button', { name: 'Helpful' })).toHaveAttribute('aria-pressed', 'true');
    await userEvent.click(screen.getByRole('button', { name: 'Helpful' }));

    expect(body).toEqual({ rating: null });
    expect(onRated).toHaveBeenCalledWith(undefined);
  });

  it('shows the error when the rating fails', async () => {
    server.use(
      http.put('/api/assistant/conversations/c1/turns/3/rating', () =>
        HttpResponse.json({ status: 404, title: 'assistant.conversation_not_found', detail: 'That conversation does not exist or has expired.' }, { status: 404 }),
      ),
    );
    const { onRated } = renderButtons(undefined);

    await userEvent.click(screen.getByRole('button', { name: 'Helpful' }));

    expect(await screen.findByText(/does not exist or has expired/)).toBeInTheDocument();
    expect(onRated).not.toHaveBeenCalled();
  });
});
```

In `Chat.test.tsx` add (with that file's existing render helper and MSW stubs for a quick answer; a quick answer's events must now carry `turn: 1`, and `done` `turn: 1`):

```tsx
  it('offers a rating once the answer is saved, not while it streams', async () => {
    // Arrange a quick answer whose answer event carries turn 1 and whose done carries conversationId c1 and turn 1,
    // exactly as the file's other quick-answer tests do.
    // ...ask the question the file's helper asks...

    expect(await screen.findByRole('group', { name: 'Rate this answer' })).toBeInTheDocument();
  });
```

Write it concretely with the helpers `Chat.test.tsx` already has (its quick-answer test is the template): the assertion that matters is that the "Rate this answer" group appears after `done`, and that no such group is present while the stream is still open (use the file's open-stream helper for the second assertion, as its "Thinking…" test does).

- [ ] **Step 2: Run them to verify they fail**

Run (from `frontend/`): `npm test -- --run src/features/assistant`
Expected: FAIL — no `turn`/`rating` on messages, no `rated` action, no `AnswerRatingButtons`.

- [ ] **Step 3: Types, the call and the mutation**

`types.ts`, append:

```ts
/** An answer's rating. Sent as null to clear it. */
export type AnswerRating = NonNullable<components['schemas']['AnswerRating']>;
```

`api/assistant.ts`, append (import `AnswerRating`; `requestVoid` is already imported):

```ts
export function rateAnswer(conversationId: string, turn: number, rating: AnswerRating | null): Promise<void> {
  return requestVoid(
    `/api/assistant/conversations/${encodeURIComponent(conversationId)}/turns/${String(turn)}/rating`,
    { method: 'PUT', body: JSON.stringify({ rating }) },
  );
}
```

`queries.ts`, append (import `rateAnswer` and `AnswerRating`):

```ts
interface RateInput {
  readonly conversationId: string;
  readonly turn: number;
  readonly rating: AnswerRating | null;
}

/** A rating changes nothing the sidebar lists, so nothing is invalidated: the chat records it itself. */
export function useRateAnswer(): UseMutationResult<void, ApiError, RateInput> {
  return useMutation({
    mutationFn: ({ conversationId, turn, rating }: RateInput) => rateAnswer(conversationId, turn, rating),
  });
}
```

- [ ] **Step 4: The reducer and the hook**

`conversation.ts`:
- `AssistantMessage` gains:

```ts
  /** The stored turn that carries this answer: what a rating names. Unknown until the answer's turn arrives. */
  readonly turn?: number | undefined;
  readonly rating?: AnswerRating | undefined;
```

- `ConversationAction` gains `| { readonly type: 'rated'; readonly turn: number; readonly rating: AnswerRating | undefined }`, and `conversationReducer` handles it:

```ts
    case 'rated':
      return {
        ...state,
        messages: state.messages.map((m) => (m.role === 'assistant' && m.turn === action.turn ? { ...m, rating: action.rating } : m)),
      };
```

- In `applyEvent`: `done` sets `turn: event.turn ?? m.turn` alongside `outcome`; `answer` sets `turn: event.turn ?? undefined` with the answer; `suggestions` sets `turn: event.turn ?? undefined` with the suggestions.
- In `fromSaved`, each message gains `turn: message.turn ?? undefined, rating: message.rating ?? undefined`.

`useAssistantConversation.ts`: add to `AssistantConversation` `readonly rated: (turn: number, rating: AnswerRating | undefined) => void;`, implement it as a `useCallback` that dispatches `{ type: 'rated', turn, rating }`, and return it.

- [ ] **Step 5: The buttons, and where they appear**

`frontend/src/features/assistant/AnswerRatingButtons.tsx`:

```tsx
import { ErrorPanel } from '../../components/ErrorPanel';
import { useRateAnswer } from './queries';
import type { AnswerRating } from './types';

interface AnswerRatingButtonsProps {
  readonly conversationId: string;
  readonly turn: number;
  readonly rating: AnswerRating | undefined;
  readonly onRated: (rating: AnswerRating | undefined) => void;
}

/** 👍/👎 on one saved answer. Clicking the chosen rating again clears it. */
export function AnswerRatingButtons({ conversationId, turn, rating, onRated }: AnswerRatingButtonsProps): React.JSX.Element {
  const rate = useRateAnswer();

  function choose(next: AnswerRating): void {
    const value = rating === next ? undefined : next;
    rate.mutate({ conversationId, turn, rating: value ?? null }, { onSuccess: () => onRated(value) });
  }

  return (
    <div className="assistant-rating" role="group" aria-label="Rate this answer">
      <button type="button" aria-label="Helpful" aria-pressed={rating === 'Up'} disabled={rate.isPending} onClick={() => choose('Up')}>
        👍
      </button>
      <button type="button" aria-label="Not helpful" aria-pressed={rating === 'Down'} disabled={rate.isPending} onClick={() => choose('Down')}>
        👎
      </button>
      {rate.error && <ErrorPanel error={rate.error} />}
    </div>
  );
}
```

`Chat.tsx`: give `MessageProps` a `readonly footer?: React.ReactNode;`, and render `{footer}` as the last child of each of `Message`'s three assistant branches (the card, the suggestions, the AI text). Where `Chat` maps messages to `<Message …>`, pass:

```tsx
                  footer={
                    conversation.conversationId !== undefined &&
                    message.role === 'assistant' &&
                    message.turn !== undefined &&
                    !(conversation.streaming && index === conversation.messages.length - 1) ? (
                      <AnswerRatingButtons
                        conversationId={conversation.conversationId}
                        turn={message.turn}
                        rating={message.rating}
                        onRated={(rating) => {
                          if (message.turn !== undefined) conversation.rated(message.turn, rating);
                        }}
                      />
                    ) : null
                  }
```

`assistant.css`: add

```css
.assistant-rating {
  display: flex;
  gap: 0.25rem;
  margin-top: 0.25rem;
}

.assistant-rating button {
  border: 1px solid transparent;
  border-radius: 0.375rem;
  background: none;
  cursor: pointer;
  opacity: 0.6;
}

.assistant-rating button[aria-pressed='true'],
.assistant-rating button:hover {
  border-color: var(--color-border);
  opacity: 1;
}
```

(use the colour token the file's other bordered controls use, if `--color-border` is not it).

- [ ] **Step 6: Run the tests to verify they pass**

Run (from `frontend/`): `npm test -- --run`, `npm run lint`, `npx tsc --noEmit -p tsconfig.app.json`.
Expected: all PASS, lint clean.

- [ ] **Step 7: Commit**

```bash
git add frontend/src
git commit -m "feat(assistant): rate an answer with a thumbs up or down"
```

---

## Task 9: The insights page

**Files:**
- Create: `frontend/src/features/monitoring/AssistantInsightsPage.tsx`, `frontend/src/features/monitoring/AssistantInsightsPage.test.tsx`
- Modify: `frontend/src/api/monitoring.ts`, `frontend/src/features/monitoring/types.ts`, `frontend/src/features/monitoring/queries.ts`, `frontend/src/routes.tsx`, `frontend/src/navigation.ts`, and any navigation/dashboard test that pins the Operations group's item count

**Interfaces:**
- Consumes: `GET /api/monitoring/assistant/insights?days=N` (Task 7).
- Produces: route `/monitoring/assistant-insights` behind `RequireRole allow="Admin"`; navigation item "Assistant insights" in the Operations group; the page's `h1` "Assistant insights", a `combobox` "Window", tables captioned "Answers by day", "Most asked", "Missed questions".

- [ ] **Step 1: Write the failing tests**

`frontend/src/features/monitoring/AssistantInsightsPage.test.tsx`:

```tsx
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { AssistantInsightsPage } from './AssistantInsightsPage';

const insights = {
  since: '2026-10-08T09:00:00Z',
  days: [{ day: '2026-10-14', instant: 6, help: 2, suggested: 1, ai: 1 }],
  targets: [{ path: 'Instant', targetId: 'order_status', questions: 6, up: 2, down: 1 }],
  misses: [{ at: '2026-10-14T10:00:00Z', path: 'Suggested', targetId: null, bestScore: 0.61, rating: null, question: 'Write me a poem about the rain' }],
};

function renderPage(): void {
  render(
    <MemoryRouter>
      <AssistantInsightsPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('AssistantInsightsPage', () => {
  it('shows the answer rate per day', async () => {
    server.use(http.get('/api/monitoring/assistant/insights', () => HttpResponse.json(insights)));
    renderPage();

    const days = await screen.findByRole('table', { name: 'Answers by day' });
    // 6 instant + 2 help of 10 answered at once
    expect(within(days).getByRole('row', { name: /2026-10-14/ })).toHaveTextContent('80%');
  });

  it('lists the missed questions', async () => {
    server.use(http.get('/api/monitoring/assistant/insights', () => HttpResponse.json(insights)));
    renderPage();

    const misses = await screen.findByRole('table', { name: 'Missed questions' });
    expect(within(misses).getByText('Write me a poem about the rain')).toBeInTheDocument();
  });

  it('asks for the chosen window', async () => {
    const asked: string[] = [];
    server.use(
      http.get('/api/monitoring/assistant/insights', ({ request }) => {
        asked.push(new URL(request.url).searchParams.get('days') ?? '');
        return HttpResponse.json(insights);
      }),
    );
    renderPage();
    await screen.findByRole('table', { name: 'Answers by day' });

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Window' }), '30');

    await screen.findByRole('table', { name: 'Answers by day' });
    expect(asked).toContain('30');
  });

  it('says so when there is nothing yet', async () => {
    server.use(http.get('/api/monitoring/assistant/insights', () => HttpResponse.json({ ...insights, days: [], targets: [], misses: [] })));
    renderPage();

    expect(await screen.findByText('No questions in this window.')).toBeInTheDocument();
  });

  it('shows the error when the insights cannot load', async () => {
    server.use(
      http.get('/api/monitoring/assistant/insights', () => HttpResponse.json({ status: 503, title: 'unavailable', detail: 'Down.' }, { status: 503 })),
    );
    renderPage();

    expect(await screen.findByText(/Down\./)).toBeInTheDocument();
  });
});
```

In `routes.test.tsx` add, beside the other administrator-route tests (with that file's admin-session stub):

```tsx
  it('opens the assistant insights for an administrator', async () => {
    // stub the admin session as the file's other admin tests do, and the insights endpoint with an empty result
    renderAt('/monitoring/assistant-insights');

    expect(await screen.findByRole('heading', { level: 1, name: 'Assistant insights' })).toBeInTheDocument();
  });
```

- [ ] **Step 2: Run them to verify they fail**

Run (from `frontend/`): `npm test -- --run src/features/monitoring/AssistantInsightsPage.test.tsx src/routes.test.tsx`
Expected: FAIL — the page and the route do not exist.

- [ ] **Step 3: The call, the types and the query**

`features/monitoring/types.ts`, append:

```ts
export type AssistantInsights = components['schemas']['AssistantInsightsResponse'];
```

(import `components` from `../../api/schema` if the file does not already).

`api/monitoring.ts`, append (import the type):

```ts
export function getAssistantInsights(days: number): Promise<AssistantInsights> {
  return request<AssistantInsights>(`/api/monitoring/assistant/insights?days=${String(days)}`);
}
```

`features/monitoring/queries.ts`: add `assistantInsights: (days: number) => [...monitoringKeys.all, 'assistant-insights', days] as const,` to `monitoringKeys`, and:

```ts
export function useAssistantInsights(days: number): UseQueryResult<AssistantInsights, ApiError> {
  return useQuery<AssistantInsights, ApiError>({
    queryKey: monitoringKeys.assistantInsights(days),
    queryFn: () => getAssistantInsights(days),
  });
}
```

- [ ] **Step 4: The page**

`frontend/src/features/monitoring/AssistantInsightsPage.tsx`:

```tsx
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { ErrorPanel } from '../../components/ErrorPanel';
import { useAssistantInsights } from './queries';
import './monitoring.css';

const Windows = [7, 30] as const;

const PathLabels: Readonly<Record<string, string>> = {
  Instant: 'Instant answer',
  Help: 'Help',
  Suggested: 'Suggestions',
  Ai: 'AI',
};

/**
 * How the assistant is doing, for administrators: how many questions it answered at once, what
 * people ask most, and the questions it missed — scrubbed, and gone when their conversation goes
 * (ADR 0037).
 */
export function AssistantInsightsPage(): React.JSX.Element {
  const [days, setDays] = useState<number>(7);
  const insights = useAssistantInsights(days);

  return (
    <section>
      <p>
        <Link to="/monitoring">← Monitoring</Link>
      </p>
      <h1>Assistant insights</h1>

      <div className="filters">
        <label htmlFor="window">
          Window
          <select id="window" value={days} onChange={(event) => setDays(Number(event.target.value))}>
            {Windows.map((value) => (
              <option key={value} value={value}>
                {`Last ${String(value)} days`}
              </option>
            ))}
          </select>
        </label>
      </div>

      {insights.error && <ErrorPanel error={insights.error} />}
      {insights.isPending && <p role="status">Loading the assistant's insights…</p>}

      {insights.isSuccess &&
        (insights.data.days.length === 0 ? (
          <p>No questions in this window.</p>
        ) : (
          <>
            <table className="runs">
              <caption>Answers by day</caption>
              <thead>
                <tr>
                  <th scope="col">Day</th>
                  <th scope="col">Instant</th>
                  <th scope="col">Help</th>
                  <th scope="col">Suggestions</th>
                  <th scope="col">AI</th>
                  <th scope="col">Answered at once</th>
                </tr>
              </thead>
              <tbody>
                {insights.data.days.map((day) => {
                  const total = day.instant + day.help + day.suggested + day.ai;
                  const atOnce = total === 0 ? 0 : Math.round(((day.instant + day.help) * 100) / total);
                  return (
                    <tr key={day.day}>
                      <td>{day.day}</td>
                      <td>{day.instant}</td>
                      <td>{day.help}</td>
                      <td>{day.suggested}</td>
                      <td>{day.ai}</td>
                      <td>{`${String(atOnce)}%`}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>

            <table className="runs">
              <caption>Most asked</caption>
              <thead>
                <tr>
                  <th scope="col">Answered by</th>
                  <th scope="col">Intent or article</th>
                  <th scope="col">Questions</th>
                  <th scope="col">👍</th>
                  <th scope="col">👎</th>
                </tr>
              </thead>
              <tbody>
                {insights.data.targets.map((target) => (
                  <tr key={`${target.path}-${target.targetId}`}>
                    <td>{PathLabels[target.path] ?? target.path}</td>
                    <td>{target.targetId}</td>
                    <td>{target.questions}</td>
                    <td>{target.up}</td>
                    <td>{target.down}</td>
                  </tr>
                ))}
              </tbody>
            </table>

            <table className="runs">
              <caption>Missed questions</caption>
              <thead>
                <tr>
                  <th scope="col">When</th>
                  <th scope="col">Question</th>
                  <th scope="col">Ended in</th>
                  <th scope="col">Rated</th>
                  <th scope="col">Best match</th>
                </tr>
              </thead>
              <tbody>
                {insights.data.misses.map((miss, index) => (
                  // No id: a miss names no conversation. Position within one response is stable.
                  <tr key={`${miss.at}-${String(index)}`}>
                    <td>{new Date(miss.at).toLocaleString()}</td>
                    <td>{miss.question}</td>
                    <td>{PathLabels[miss.path] ?? miss.path}</td>
                    <td>{miss.rating === 'Down' ? '👎' : miss.rating === 'Up' ? '👍' : '—'}</td>
                    <td>{miss.bestScore == null ? '—' : miss.bestScore.toFixed(2)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </>
        ))}
    </section>
  );
}
```

(If lint flags the nested ternary in the "Rated" cell, extract a small `ratingLabel(rating)` function in the same file.)

- [ ] **Step 5: The route and the navigation**

`routes.tsx`: import the page and add, inside the `RequireRole allow="Admin"` block beside the other `/monitoring/*` routes:

```tsx
            <Route path="/monitoring/assistant-insights" element={<AssistantInsightsPage />} />
```

`navigation.ts`, in the Operations group's `items`, after Monitoring:

```ts
      {
        label: 'Assistant insights',
        to: '/monitoring/assistant-insights',
        description: 'What the assistant answered, and what it missed',
      },
```

Run the navigation and dashboard tests; update any that pin the Operations group's item count or list (the new item is expected, not a regression).

- [ ] **Step 6: Run the tests to verify they pass**

Run (from `frontend/`): `npm test -- --run`, `npm run lint`, `npx tsc --noEmit -p tsconfig.app.json`, `npm run build`.
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add frontend/src
git commit -m "feat(assistant): an insights page for administrators"
```

---

## Task 10: End to end

**Files:**
- Create: `frontend/e2e/specs/assistant/insights.spec.ts`
- Modify: `frontend/e2e/screens/assistant.ts`, `frontend/e2e/CLAUDE.md` (the coverage row and the test counts)

**Interfaces:**
- Consumes: the "Rate this answer" group and its buttons (Task 8); the insights page and its navigation entry (Task 9); the exact unsure phrase `Write me a poem about the rain` (the evaluation set labels it unsure, so the instant path answers it with suggestions).

- [ ] **Step 1: Screen locators**

Append to `frontend/e2e/screens/assistant.ts`:

```ts
/** The 👎 on the newest answer. */
export const notHelpful = (p: Page): Locator =>
  conversation(p).getByRole('group', { name: 'Rate this answer' }).last().getByRole('button', { name: 'Not helpful' });
```

Create `frontend/e2e/screens/assistantInsights.ts`:

```ts
import type { Locator, Page } from '@playwright/test';

export const missedQuestions = (p: Page): Locator => p.getByRole('table', { name: 'Missed questions' });
```

- [ ] **Step 2: The spec**

`frontend/e2e/specs/assistant/insights.spec.ts`:

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as assistant from '../../screens/assistant.ts';
import * as insights from '../../screens/assistantInsights.ts';
import * as shell from '../../screens/shell.ts';

// @local-only: needs instant answers and the fake Messages API, which only the managed stack runs.
test.describe('assistant insights', { tag: '@local-only' }, () => {
  test("a member's missed question, rated down, reaches an administrator's insights", async ({ signedInPage, adminPage }) => {
    await test.step('a member asks something the assistant cannot answer at once, and rates the reply down', async () => {
      await signedInPage.goto('/assistant');
      await assistant.ask(signedInPage, 'Write me a poem about the rain');
      await assistant.notHelpful(signedInPage).click();
      await expect(assistant.notHelpful(signedInPage)).toHaveAttribute('aria-pressed', 'true');
    });

    await test.step('an administrator sees it among the missed questions', async () => {
      await adminPage.goto('/');
      await shell.goTo(adminPage, 'Operations', 'Assistant insights');
      await expect(insights.missedQuestions(adminPage)).toContainText('Write me a poem about the rain');
    });
  });
});
```

Check `assistant.ask` and `shell.goTo`'s actual names and signatures in `e2e/screens/assistant.ts` and `e2e/screens/shell.ts` and use them as they are.

- [ ] **Step 3: Run it, then the suite**

Run (from `frontend/`, nothing on 5234/5235): `npm run e2e -- --grep "assistant insights"`, then `npm run e2e`.
Expected: the new test passes; the whole suite passes. Update `frontend/e2e/CLAUDE.md`'s Assistant coverage row (add "an administrator sees a member's missed, rated-down question on the insights page") and the total test count; the kind count is unchanged (the spec is `@local-only`).

- [ ] **Step 4: Commit**

```bash
git add frontend/e2e
git commit -m "feat(assistant): cover rating and insights end to end"
```

---

## Task 11: Record, verify, review, and open the PR

**Files:**
- Create: `docs/adr/0037-administrators-read-missed-assistant-questions.md`
- Modify: `docs/adr/0034-the-assistant-for-every-user.md` (status line), `.claude/skills/assistant/SKILL.md`, `CLAUDE.md` (the Assistant rule), `docs/superpowers/specs/2026-10-12-assistant-instant-answers-design.md` (the four rulings at the top of this plan)

- [ ] **Step 1: ADR 0037**

Use the `adr` command's conventions (`docs/adr/0036-*.md` is the closest model). Title: **Administrators read missed assistant questions**. Status: Accepted; amends 0034. It must say:
- **Context:** instant answers (ADR 0036) are tuned on a hand-written evaluation set; improving them needs the questions real users asked that the assistant missed. Those are members' own words — personal data under ADR 0034.
- **Decision:** members rate answers 👍/👎; every answered and saved question leaves an outcome row (path, target, best score, margin, whether the spelling correction won, scope) with **no question text and no user id**; administrators (Monitoring.Read) see daily rates, the most-asked targets and up to 50 missed questions (ended in suggestions or the AI, or rated 👎) from the last 1–30 days; **the question text is read live from the transcript and scrubbed with `OperationalText.Scrub`**, never copied, and the list names no user and no conversation; ratings and outcomes cascade with the conversation, so the owner's delete and the 30-day retention remove them.
- **Consequences:** a new, narrow access path to members' words (scrubbed, read-only, admin-only); the insights page covers at most 30 days; a misses list cannot link to the conversation (administrators cannot open another user's conversation); the turn number becomes part of the API (events and saved messages).
- **Alternatives:** storing the question text in the outcome (outlives the transcript, so rejected); hashing questions (unreadable, useless for improving the catalog); showing misses to their author only (no one could improve the catalog from them); no ratings (precision misses with no signal).

Set ADR 0034's status to "Accepted; amended by 0036 and 0037" (keep whatever amendment it already lists).

- [ ] **Step 2: The skill, CLAUDE.md and the spec**

- `.claude/skills/assistant/SKILL.md`: add a section **Feedback and insights (ADR 0037)**: an answer's turn (the quick reply's own turn, an AI answer's final model turn — `AnswerTurns.Of`); the rating endpoint and that a rating is owner-only and never in the transcript; one outcome row per saved question, written by both handlers after `Append`; the insights query and that its question text is read live and scrubbed; ratings and outcomes cascade with the conversation.
- `CLAUDE.md`, append to the **Assistant** paragraph under "Rules that fail silently": `Ratings and outcome rows (ADR 0037) never hold question text or a user id; the insights page reads question text live from the transcript through OperationalText.Scrub, so it disappears with the conversation.`
- The spec: under its "Rulings made in the plan", add this plan's four rulings as items 10–13 (numbering after the existing ones).

```bash
git add docs .claude/skills/assistant/SKILL.md CLAUDE.md
git commit -m "docs(assistant): record that administrators read missed questions"
```

- [ ] **Step 3: Verify**

Invoke the `verify` skill. Expected: backend format, Debug build, every test project, frontend lint/build/tests, both codegen trees and the contract unchanged (all already regenerated in their tasks), e2e and the hook tests all green. Report what ran; a skipped step is not a pass.

- [ ] **Step 4: Review**

Dispatch the `dotnet-reviewer` agent on `git diff main...HEAD -- src tests` and the `react-reviewer` agent on `git diff main...HEAD -- frontend`; fix what they raise.

- [ ] **Step 5: Ask before publishing**

Ask the user before pushing. Then `git push -u origin claude/assistant-feedback-insights` and `gh pr create --base main --title "feat(assistant): rate answers and show administrators what the assistant missed"` with a body that has a summary, the rulings, the privacy boundary (ADR 0037), what was verified, and ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

---

## Self-Review

**Spec coverage.** Ratings (`PUT …/turns/{n}/rating`, `up`/`down`/`null`, owner only, own table, never in the transcript) → Tasks 3, 5, 8. Outcome log (one row per question; conversation id, turn, time, path, intent or article id, best score, margin, correction won, scope; no question text, no user id; deleted with the conversation) → Tasks 1, 3, 4. Insights (`/monitoring/assistant-insights`, administrators only, Operations navigation entry; rate over time by path and by intent; misses list read live and scrubbed) → Tasks 7, 9. ADR 0037 amending 0034 → Task 11. Tests: unit and integration per layer in each task; frontend ratings (Task 8); e2e "an administrator sees that miss on the insights page" (Task 10). The four rulings → Tasks 2 and 6 (turn on events and saved messages), Task 3's `AnswerTurns` (final model turn), Task 4 (outcomes only on saved questions).

**Placeholders.** Two test steps (Task 8's `Chat.test.tsx` case, Task 9's `routes.test.tsx` case) say to use the file's existing helpers rather than spelling out a harness this plan cannot see; each names exactly what to arrange and what to assert. Everything else is literal code.

**Type consistency.** `AssistantAnswer(ConversationId, Outcome, Turn)` (Tasks 2, 4, 5); `IAssistantOutput.AnswerAsync/SuggestionsAsync(…, int turn, …)` (Task 2); `RouteDecision.Evidence` → `RouteEvidence(BestScore, Margin, Corrected)` (Tasks 1, 4); `AnswerRating { Up, Down }`, `AnswerPath { Instant, Help, Suggested, Ai }`, `RateResult` (Tasks 3, 5, 6, 7); `AnsweredQuestion` (Tasks 3, 4, 7); `AnswerTurns.Of` (Tasks 3, 6 via `AssistantDisplay`'s own tracking — both define the answer turn as the final model turn or the quick turn); `AssistantDisplayMessage(…, Turn, Rating)` and `AssistantMessageResponse.Turn/.Rating` (Tasks 6, 8); `GetAssistantInsights(Days)` → `AssistantInsightsView` → `AssistantInsightsResponse` (`days`, `targets`, `misses`) (Tasks 7, 9, 10); frontend `AnswerRating`, `rateAnswer`, `useRateAnswer`, `rated` action, `AnswerRatingButtons` (Task 8), "Rate this answer"/"Not helpful" (Tasks 8, 10), "Missed questions" (Tasks 9, 10).

**Review Focus.** Each of the five lines has its test in the named task: 1 → Task 3 `RateAsync_ATurnThatIsNotAnAnswer…` and Task 5 `Rate_TheQuestionTurn_IsBadRequest`; 2 → Task 3 `…AnotherOwnersConversation…` and Task 5 `Rate_AnotherUsersAnswer_IsNotFound`; 3 → Task 3 `DeletingTheConversation…` and Task 7 `MissesAsync_ADeletedConversation_IsGone`; 4 → Task 7 `HandleAsync_ScrubsEveryMissedQuestion`; 5 → Task 8 `offers a rating once the answer is saved, not while it streams` and `clears the rating when the chosen one is clicked again`.
