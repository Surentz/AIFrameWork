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
