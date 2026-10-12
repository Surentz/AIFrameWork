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
