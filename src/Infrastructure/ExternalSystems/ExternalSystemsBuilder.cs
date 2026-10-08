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

        var system = Snapshot.Find(name);

        var client = Services.AddRefitGeneratedClient<TApi>()
            .ConfigureHttpClient((sp, http) =>
            {
                var current = sp.GetRequiredService<IOptionsMonitor<ExternalSystemsOptions>>().CurrentValue.Find(name);
                http.BaseAddress = new Uri($"{(current?.BaseAddress ?? NotConfiguredAddress).TrimEnd('/')}/");
                http.Timeout = Timeout.InfiniteTimeSpan; // the standard handler owns both timeouts.
            })
            .AddHttpMessageHandler(sp => new OutboundTrafficHandler(
                sp.GetRequiredService<ITrafficRecorder>(), sp.GetRequiredService<TimeProvider>(), name, TrafficKind.Outbound));

        client.AddStandardResilienceHandler().Configure((resilience, sp) => ConfigureStandardResilience(resilience, sp, name));

        client.AddHttpMessageHandler(sp => new OutboundTrafficHandler(
            sp.GetRequiredService<ITrafficRecorder>(), sp.GetRequiredService<TimeProvider>(), name, TrafficKind.OutboundAttempt));

        if (system is { Auth.Kind: not ExternalSystemAuthKind.None })
        {
            AddTokenResend(client);
            client.AddClientCredentialsTokenHandler(ClientCredentialsClientName.Parse(name));
        }

        client.ConfigurePrimaryHttpMessageHandler(sp =>
            sp.GetRequiredService<ExternalSystemHandlerFactory>().CreatePrimaryHandler(name));

        return new ExternalSystemClientBuilder<TApi>(Services, name);
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

        var globallyEnabled = sp.GetRequiredService<IOptions<ResilienceOptions>>().Value.Enabled;
        var disabledReason = sp.GetRequiredService<IOptionsMonitor<ExternalSystemClientSettings>>().Get(name).RetryDisabledReason;
        if (disabledReason is not null)
        {
            // This callback runs once, when the client's pipeline is first built.
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
