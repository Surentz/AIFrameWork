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
    public void Route_UsesTheSpellingCorrection_WhenItYieldsAnInstantMatchTheOriginalDidNot()
    {
        // "ordres" is no catalog word, so the original question is unsure; its correction is
        // "show my recent orders", which clears the threshold.
        var embedder = Embedder()
            .Add("show my recent ordres", 0.3f, 0.3f, 0.3f, 0.3f)
            .Add("show my recent orders", 0.98f, 0.1f, 0, 0);

        var decision = Router(embedder).Route("show my recent ordres", ToolScope.Member).Value;

        var routed = decision.Should().BeOfType<RouteToIntent>().Subject;
        routed.Intent.Id.Should().Be(QuickIntents.RecentOrders);
        routed.AnsweredAs.Should().Be("show my recent orders");
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
