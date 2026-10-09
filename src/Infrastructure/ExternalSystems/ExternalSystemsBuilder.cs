using System.Net;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems.Http;
using AiFramework.Infrastructure.Resilience;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.DPoP;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Refit;

namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// Returned by AddExternalSystems. Carries the registration-time binding, because what to
/// register — a token handler, a certificate check — depends on configuration that has to be
/// known before the container exists (the same reason the worker reads Jobs off configuration).
/// </summary>
public sealed partial class ExternalSystemsBuilder
{
    // Never resolvable (RFC 2606 .invalid). Used only for a name with no configuration, whose
    // primary handler fails fast as "not configured" before any request leaves the process.
    private const string NotConfiguredAddress = "https://not-configured.invalid";

    private const string TokenResendPipeline = "token-resend";

    private readonly HashSet<Type> _clientTypes = [];

    internal ExternalSystemsBuilder(IServiceCollection services, ExternalSystemsOptions snapshot)
    {
        Services = services;
        Snapshot = snapshot;
    }

    public IServiceCollection Services { get; }

    internal ExternalSystemsOptions Snapshot { get; }

    /// <summary>
    /// A Refit client for one system, with the fixed chain, outermost first:
    /// Outbound traffic → standard resilience (this system's options and breaker) →
    /// OutboundAttempt traffic → our 401 resend and Duende's token handler (when Auth.Kind != None) →
    /// the primary handler presenting the certificate. ADR 0031; order asserted by HandlerChainTests.
    /// </summary>
    /// <remarks>
    /// An unconfigured name still registers: its primary handler fails fast as "not configured",
    /// which the adapter maps to Unavailable — so an environment without a partner degrades one
    /// feature instead of refusing to start.
    /// </remarks>
    public ExternalSystemClientBuilder<TApi> AddClient<TApi>(string name)
        where TApi : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_clientTypes.Add(typeof(TApi)))
        {
            // One Refit interface is one named HttpClient: a second AddClient would silently append
            // a second handler set to the first one's chain.
            throw new InvalidOperationException(
                $"{typeof(TApi).Name} is already registered as an external-system client; each system needs its own interface.");
        }

        // Configuration keys are case-insensitive but named options (Duende's token client, our
        // client settings) match ordinally: an env var's PARTNERSIM and the code's "PartnerSim"
        // are one system only if every name below is the configuration's own spelling — the one
        // AddExternalSystems registered the token client and the health checks under.
        var systemName = Snapshot.CanonicalName(name);
        var system = Snapshot.Find(systemName);

        var client = Services.AddRefitGeneratedClient<TApi>()
            .ConfigureHttpClient((sp, http) =>
            {
                var current = sp.GetRequiredService<IOptionsMonitor<ExternalSystemsOptions>>().CurrentValue.Find(systemName);
                http.BaseAddress = new Uri($"{(current?.BaseAddress ?? NotConfiguredAddress).TrimEnd('/')}/");
                http.Timeout = Timeout.InfiniteTimeSpan; // the standard handler owns both timeouts.
            })
            .AddHttpMessageHandler(sp => new OutboundTrafficHandler(
                sp.GetRequiredService<ITrafficRecorder>(), sp.GetRequiredService<TimeProvider>(), systemName, TrafficKind.Outbound,
                sp.GetRequiredService<ExternalSystemMetrics>()));

        client.AddStandardResilienceHandler().Configure((resilience, sp) => ConfigureStandardResilience(resilience, sp, systemName));

        client.AddHttpMessageHandler(sp => new OutboundTrafficHandler(
            sp.GetRequiredService<ITrafficRecorder>(), sp.GetRequiredService<TimeProvider>(), systemName, TrafficKind.OutboundAttempt));

        if (system is { Auth.Kind: not ExternalSystemAuthKind.None })
        {
            AddTokenResend(client);
            client.AddClientCredentialsTokenHandler(ClientCredentialsClientName.Parse(systemName));
        }

        client.ConfigurePrimaryHttpMessageHandler(sp =>
            sp.GetRequiredService<ExternalSystemHandlerFactory>().CreatePrimaryHandler(systemName));

        return new ExternalSystemClientBuilder<TApi>(Services, systemName);
    }

    /// <summary>This system's timeouts and retry budget; retry off globally, by WithoutRetry, or when unconfigured.</summary>
    private static void ConfigureStandardResilience(HttpStandardResilienceOptions resilience, IServiceProvider sp, string name)
    {
        var current = sp.GetRequiredService<IOptionsMonitor<ExternalSystemsOptions>>().CurrentValue.Find(name);
        var options = current ?? new ExternalSystemOptions();
        resilience.TotalRequestTimeout.Timeout = options.Resilience.TotalRequestTimeout;
        resilience.AttemptTimeout.Timeout = options.Resilience.AttemptTimeout;
        resilience.Retry.MaxRetryAttempts = options.Resilience.MaxRetryAttempts;
        resilience.Retry.Delay = options.Resilience.BaseDelay;

        // HttpStandardResilienceOptionsCustomValidator (ValidateOnStart) requires
        // CircuitBreaker.SamplingDuration >= 2 x AttemptTimeout. Against the 30 s default, any
        // AttemptTimeout over 15 s would stop the host with a message naming neither the system
        // nor the property, so the window widens with the attempt instead.
        var minimumSampling = options.Resilience.AttemptTimeout * 2;
        if (resilience.CircuitBreaker.SamplingDuration < minimumSampling)
        {
            resilience.CircuitBreaker.SamplingDuration = minimumSampling;
        }

        // The 401 resend sets Duende's force-renewal flag on the request, and every later attempt
        // of the same call reuses that request: clear it before each standard retry, or each
        // retry would fetch a fresh token. Here, not in the resend pipeline, because only this
        // callback runs before every later attempt however the previous one ended, including a
        // resend cut off by the attempt timeout.
        resilience.Retry.OnRetry = arguments =>
        {
            arguments.Context.GetRequestMessage()?.SetForceRenewal(false);
            return ValueTask.CompletedTask;
        };

        var globallyEnabled = sp.GetRequiredService<IOptions<ResilienceOptions>>().Value.Enabled;
        var disabledReason = sp.GetRequiredService<IOptionsMonitor<ExternalSystemClientSettings>>().Get(name).RetryDisabledReason;
        if (disabledReason is not null)
        {
            // This callback runs once per container, when these named options are first built: at
            // host start, because the standard handler registers them ValidateOnStart (a bare
            // container that never runs startup validation builds them at the first call).
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AiFramework.ExternalSystems");
            LogRetryDisabled(logger, name, disabledReason);
        }

        // An unconfigured system fails fast in its primary handler with an HttpRequestException,
        // which the standard predicate treats as transient: retrying it would only add backoff
        // and feed the breaker.
        if (!globallyEnabled || disabledReason is not null || current is null)
        {
            // Never MaxRetryAttempts = 0: Polly's own validation forbids it. ADR 0014.
            resilience.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
        }
    }

    /// <summary>
    /// One immediate resend after a 401, with the token handler told to fetch a fresh token —
    /// what Duende's AddDefaultAccessTokenResiliency() does, minus its delay. Duende's inherits
    /// HttpRetryStrategyOptions' 2 s exponential, jittered base (up to about 2.8 s), and that wait
    /// runs inside the standard handler's AttemptTimeout, which must also cover the 401, a token
    /// fetch and the resend. With the standard retry off (WithoutRetry, Resilience:Enabled=false)
    /// nothing outside would rescue a timed-out attempt. It runs whatever the standard retry
    /// says: a 401 means the partner did not act on the request, so a non-idempotent call is
    /// safe to resend. No DPoP nonce handling: no system here uses DPoP.
    /// </summary>
    private static void AddTokenResend(IHttpClientBuilder client) =>
        client.AddResilienceHandler(TokenResendPipeline, pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 1,
            Delay = TimeSpan.Zero,
            BackoffType = DelayBackoffType.Constant,
            UseJitter = false,

            // On by default: a 401 carrying Retry-After would otherwise wait out the header inside
            // the attempt timeout, the very delay this pipeline exists to avoid.
            ShouldRetryAfterHeader = false,
            ShouldHandle = arguments =>
            {
                if (arguments.Outcome.Result is { StatusCode: HttpStatusCode.Unauthorized, RequestMessage: { } request })
                {
                    // Public Duende API; its ClientCredentialsTokenRetriever reads it into
                    // TokenRequestParameters.ForceTokenRenewal, bypassing the cached token.
                    request.SetForceRenewal(true);
                    return ValueTask.FromResult(true);
                }

                return ValueTask.FromResult(false);
            },
        }));

    [LoggerMessage(Level = LogLevel.Information, Message = "Retry is disabled for {System}: {Reason}")]
    private static partial void LogRetryDisabled(ILogger logger, string system, string reason);
}
