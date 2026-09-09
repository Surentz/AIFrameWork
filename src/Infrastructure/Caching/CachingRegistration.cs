using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Caching;

public static class CachingRegistration
{
    /// <summary>
    /// The cache store and its options. Called from AddInfrastructure, beside AddOutbox, so the
    /// store and its options are wired by AddInfrastructure; the only thing Api names directly is
    /// CacheOptions itself, to bind the section.
    /// </summary>
    /// <remarks>
    /// Deliberately does not bind configuration itself. Binding here would make
    /// IOptions&lt;CacheOptions&gt; depend on an IConfiguration being registered, which a bare
    /// ServiceCollection in a unit test does not have — and the caching behavior resolves those
    /// options on every query. Program.cs binds the section instead, the same way it already
    /// reads Wolverine:Durable and RateLimiting:Auth and passes them in explicitly.
    /// </remarks>
    public static IServiceCollection AddCaching(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Validated for the same reason OutboxOptions is: a value that is merely wrong rather
        // than malformed would otherwise do something quietly useless instead of failing.
        services.AddOptions<CacheOptions>()
            .Validate(
                o => o.MaximumDuration > TimeSpan.Zero,
                "CacheOptions.MaximumDuration must be positive.")
            .Validate(
                o => o.MaximumDuration <= TimeSpan.FromHours(1),
                "CacheOptions.MaximumDuration above an hour is almost certainly a mistake.");

        // MaximumPayloadBytes bounds a single entry. The key space itself is unbounded in
        // principle — any cursor string is a distinct key — and bounded in practice by the
        // duration cap above. See ADR 0009's accepted risks.
        services.AddHybridCache(o => o.MaximumPayloadBytes = 1024 * 1024);

        return services;
    }
}
