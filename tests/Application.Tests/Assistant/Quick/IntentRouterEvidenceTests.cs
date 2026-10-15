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
