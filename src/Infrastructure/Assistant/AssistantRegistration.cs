using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using AiFramework.Application.Assistant.Quick;
using AiFramework.Infrastructure.Assistant.Quick;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.ExternalSystems.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Assistant;

public static class AssistantRegistration
{
    /// <summary>
    /// The model adapter and its HttpClient. Options are bound by the host (the API), like
    /// CacheOptions; an unbound host gets the defaults, Enabled=false, and never calls it.
    /// </summary>
    /// <remarks>
    /// The HttpClient's own timeout is infinite because the SDK's (10 minutes per attempt) governs:
    /// the HttpClient default of 100 s would cut a long answer, as the SDK's documentation warns.
    /// </remarks>
    public static IServiceCollection AddAssistant(this IServiceCollection services)
    {
        services.AddSingleton<AssistantMetrics>();
        services.AddScoped<IAssistantModel, ClaudeAssistantModel>();

        services.AddHttpClient(ClaudeAssistantModel.HttpClientName, http => http.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(sp => new OutboundTrafficHandler(
                sp.GetRequiredService<ITrafficRecorder>(),
                sp.GetRequiredService<TimeProvider>(),
                ClaudeAssistantModel.SystemName,
                TrafficKind.Outbound,
                sp.GetService<ExternalSystemMetrics>()));

        // The embedding model for instant answers (ADR 0036). A singleton that loads on first use:
        // the worker registers it too (this method is shared) but never embeds, so never loads it.
        // Options are bound by the API; an unbound host gets the default path.
        services.AddOptions<QuickModelOptions>();
        services.AddSingleton<ITextEmbedder, OnnxTextEmbedder>();

        return services;
    }
}
