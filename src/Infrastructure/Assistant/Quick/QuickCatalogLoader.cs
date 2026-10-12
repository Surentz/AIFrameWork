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
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogReady(logger, built.Value.Index.Count, built.Value.Spelling.VocabularySize, elapsed);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Instant answers are unavailable: {Reason} Run scripts/fetch-assistant-model.ps1 to fetch the model.")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Instant answers ready: {Vectors} vectors, {Words} words, built in {Milliseconds:0} ms.")]
    private static partial void LogReady(ILogger logger, int vectors, int words, double milliseconds);
}
