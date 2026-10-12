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
    // Measured 2026-10-13 with Threshold 0.80 Margin 0.04: instant 0.945 (69/73), typos 1.00 (10/10).
    // Each floor is the measured rate minus 0.05, rounded down to two places.
    private const double InstantFloor = 0.89;
    private const double TypoFloor = 0.95;

    private const string UnsureLabel = "unsure";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
            Json) ?? [];

        var outcome = new Outcome([], 0, 0, 0, 0, 0, 0);
        foreach (var c in cases)
        {
            var scope = string.Equals(c.Scope, "operator", StringComparison.Ordinal) ? ToolScope.Operator : ToolScope.Member;
            var got = router.Route(c.Question, scope).Value switch
            {
                RouteToIntent intent => intent.Intent.Id,
                RouteToHelp help => $"help:{help.Section.ArticleId}",
                _ => UnsureLabel,
            };

            if (string.Equals(c.Expect, UnsureLabel, StringComparison.Ordinal))
            {
                outcome = outcome with { Unsure = outcome.Unsure + 1, UnsureHeld = outcome.UnsureHeld + (string.Equals(got, UnsureLabel, StringComparison.Ordinal) ? 1 : 0) };
                if (!string.Equals(got, UnsureLabel, StringComparison.Ordinal))
                {
                    outcome.Wrong.Add($"[{c.Scope}] '{c.Question}': expected unsure, answered {got}");
                }

                continue;
            }

            outcome = outcome with { InCatalog = outcome.InCatalog + 1, Typos = outcome.Typos + (c.Typo ? 1 : 0) };
            if (string.Equals(got, UnsureLabel, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(got, c.Expect, StringComparison.Ordinal))
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
