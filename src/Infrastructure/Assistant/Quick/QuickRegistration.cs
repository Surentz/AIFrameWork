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
