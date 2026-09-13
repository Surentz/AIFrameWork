using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Resilience;

public static class ResilienceRegistration
{
    /// <summary>
    /// The resilience options and their validation. Called from AddInfrastructure, beside
    /// AddCaching, so a host wires the retry budget by calling one method rather than by
    /// remembering two. The only thing Api names directly is ResilienceOptions itself, to bind
    /// the section.
    /// </summary>
    /// <remarks>
    /// Deliberately does not bind configuration itself, for the same reason AddCaching does
    /// not: binding here would make IOptions&lt;ResilienceOptions&gt; depend on an
    /// IConfiguration being registered, which a bare ServiceCollection in a unit test does not
    /// have — and the typed clients resolve these options while the host is starting. Program.cs
    /// binds the section instead, the same way it already binds Cache and reads
    /// Wolverine:Durable and RateLimiting:Auth.
    /// </remarks>
    public static IServiceCollection AddResilience(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Validated for the same reason CacheOptions and OutboxOptions are: a value that is
        // merely wrong rather than malformed would otherwise do something quietly useless
        // instead of failing. Two of these are worse than useless, and both are called out
        // below — a pipeline that cannot be built, and one that silently never retries.
        services.AddOptions<ResilienceOptions>()
            .Validate(
                o => o.TotalRequestTimeout > TimeSpan.Zero,
                "ResilienceOptions.TotalRequestTimeout must be positive.")
            .Validate(
                o => o.AttemptTimeout > TimeSpan.Zero,
                "ResilienceOptions.AttemptTimeout must be positive.")

            // The standard handler checks this itself and throws while BUILDING the pipeline,
            // which makes it a startup failure with a message that names neither property. That
            // is the exact shape of breakage this repository has been bitten by before — green
            // build, dead host — so it is caught here first, by name.
            .Validate(
                o => o.AttemptTimeout <= o.TotalRequestTimeout,
                "ResilienceOptions.AttemptTimeout must not exceed TotalRequestTimeout: the " +
                "per-attempt timeout runs inside the total one, so an attempt that may outlive " +
                "the whole call can never fire.")

            // Zero is legitimate — it is how a non-idempotent write integration opts out of
            // retry (ADR 0014) — so this rejects only the negative, which would otherwise be
            // clamped to zero by Polly and silently mean something the author did not write.
            .Validate(
                o => o.MaxRetryAttempts >= 0,
                "ResilienceOptions.MaxRetryAttempts must not be negative.")
            .Validate(
                o => o.BaseDelay > TimeSpan.Zero,
                "ResilienceOptions.BaseDelay must be positive.")

            // Checks the SCHEME, not merely that the value is an absolute URI. Absolute is the
            // obvious test and it is not enough: on Linux, Uri.TryCreate("/rates",
            // UriKind.Absolute, ...) succeeds and yields a file:// URI, so a relative path
            // configured by mistake would pass, and HttpClient would then fail at the first
            // request on an unsupported scheme rather than at startup. Caught by
            // ResilienceRegistrationTests, which is why the theory carries "/rates".
            .Validate(
                o => Uri.TryCreate(o.ExchangeRateBaseAddress, UriKind.Absolute, out var uri)
                    && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                        || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)),
                "ResilienceOptions.ExchangeRateBaseAddress must be an absolute http or https URI.")

            // ValidateOnStart, unlike CacheOptions and OutboxOptions, and the difference is not
            // an inconsistency. Those two are resolved early by construction — CachedAsync reads
            // CacheOptions on every query, and AddOutbox's channel factory reads OutboxOptions
            // while the host builds — so a bad value there surfaces immediately anyway. Nothing
            // resolves these options until a typed client makes a request, so without this a
            // misconfigured budget would first surface as a failed REQUEST, in production,
            // rather than as a host that refuses to start.
            .ValidateOnStart();

        return services;
    }
}
