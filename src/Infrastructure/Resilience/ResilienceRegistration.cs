using AiFramework.Application.Rates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

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

            // >= 1, not >= 0: Polly's own retry-strategy validation rejects zero. See
            // ResilienceOptions.MaxRetryAttempts's remarks for why, and Enabled's for what
            // actually opts a client out of retrying.
            .Validate(
                o => o.MaxRetryAttempts >= 1,
                "ResilienceOptions.MaxRetryAttempts must be at least 1: Polly's own retry " +
                "strategy rejects zero, so this is not how a client opts out of retrying — " +
                "see ResilienceOptions.Enabled instead.")
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

    /// <summary>
    /// ADR 0014's reference typed client: <see cref="IExchangeRateProvider"/>, resolved to
    /// <see cref="ExchangeRateClient"/>, with the standard resilience handler attached. Called
    /// from AddInfrastructure, alongside AddResilience.
    /// </summary>
    /// <remarks>
    /// <see cref="ResilienceOptions"/> is resolved LAZILY, inside the two callbacks below, which
    /// both run when <c>IHttpClientFactory</c> first builds this client's handler pipeline (on
    /// first resolution, then cached) — never at THIS method's own call time, when the
    /// container is not yet built. That is what "the typed clients resolve these options while
    /// the host is starting", on <see cref="AddResilience"/>'s own remarks, refers to.
    /// <para>
    /// <see cref="ResilienceOptions.Enabled"/> is the one exception to "resolved lazily,
    /// configured freely": it cannot make <c>AddStandardResilienceHandler()</c> itself
    /// conditional, because that decision has to be made at this call site, synchronously,
    /// before any option is resolvable — there is no supported hook to attach a resilience
    /// handler "later, maybe". So the handler is always attached, and <c>Enabled</c> instead
    /// suppresses the retry strategy's <c>ShouldHandle</c> predicate inside the callback below —
    /// NOT <c>MaxRetryAttempts = 0</c>, which reads as the obvious approach and throws an
    /// <c>OptionsValidationException</c> the first time a request is made, because Polly's own
    /// validation on that property requires at least 1. See
    /// <see cref="ResilienceOptions.Enabled"/>'s own remarks for why that is the one strategy
    /// worth reaching this way and the other four are left at their defaults regardless.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddExchangeRateClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<IExchangeRateProvider, ExchangeRateClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ResilienceOptions>>().Value;

            // TrimEnd + re-append, not a bare assignment: Uri's own combining rule drops
            // BaseAddress's last path segment when it lacks a trailing slash, silently turning
            // "https://host/v1" + "latest" into "https://host/latest". Normalizing here means a
            // host that configures the address either way still gets the right request URI.
            client.BaseAddress = new Uri($"{options.ExchangeRateBaseAddress.TrimEnd('/')}/");
        })
            // Order is fixed by AddStandardResilienceHandler itself and is not to be rearranged:
            // rate limiter, then total request timeout, then retry, then circuit breaker, then
            // per-attempt timeout — outermost to innermost. The total timeout sits OUTSIDE the
            // retry so the retry budget can never outlive the caller's patience; the attempt
            // timeout sits INSIDE it so one hung socket cannot consume the whole budget. ADR 0014.
            .AddStandardResilienceHandler()
            .Configure((options, sp) =>
            {
                var resilience = sp.GetRequiredService<IOptions<ResilienceOptions>>().Value;

                options.TotalRequestTimeout.Timeout = resilience.TotalRequestTimeout;
                options.AttemptTimeout.Timeout = resilience.AttemptTimeout;
                options.Retry.MaxRetryAttempts = resilience.MaxRetryAttempts;
                options.Retry.Delay = resilience.BaseDelay;

                // When disabled, reject every outcome rather than zero MaxRetryAttempts, which
                // Polly's own validation on that property forbids. See
                // ResilienceOptions.Enabled's remarks for the full story - discovered by that
                // exact validation exception failing every retry-based case in
                // ExchangeRateClientTests.
                if (!resilience.Enabled)
                {
                    options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
                }

                // ShouldRetryAfterHeader, MaxDelay, BackoffType, UseJitter, and the retry
                // predicate's DEFAULT shape (5xx, 408, 429, HttpRequestException,
                // TimeoutRejectedException, but never an ordinary 4xx) are all left at the
                // standard handler's own defaults when Enabled is true — see the design spec's
                // table for what those are. Nothing about this reference integration needed any
                // of them changed, and a value this repository has never had reason to override
                // is not a value worth a configuration knob for.
            });

        return services;
    }
}
