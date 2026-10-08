using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems.Http;
using AiFramework.Infrastructure.Resilience;
using Duende.AccessTokenManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    /// OutboundAttempt traffic → Duende's 401-resend and token handler (when Auth.Kind != None) →
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

        client.AddStandardResilienceHandler().Configure((resilience, sp) =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<ExternalSystemsOptions>>().CurrentValue.Find(name)
                ?? new ExternalSystemOptions();
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

            if (!globallyEnabled || disabledReason is not null)
            {
                // Never MaxRetryAttempts = 0: Polly's own validation forbids it. ADR 0014.
                resilience.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
            }
        });

        client.AddHttpMessageHandler(sp => new OutboundTrafficHandler(
            sp.GetRequiredService<ITrafficRecorder>(), sp.GetRequiredService<TimeProvider>(), name, TrafficKind.OutboundAttempt));

        if (system is { Auth.Kind: not ExternalSystemAuthKind.None })
        {
            client.AddDefaultAccessTokenResiliency()
                .AddClientCredentialsTokenHandler(ClientCredentialsClientName.Parse(name));
        }

        client.ConfigurePrimaryHttpMessageHandler(sp =>
            sp.GetRequiredService<ExternalSystemHandlerFactory>().CreatePrimaryHandler(name));

        return new ExternalSystemClientBuilder<TApi>(Services, name);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retry is disabled for {System}: {Reason}")]
    private static partial void LogRetryDisabled(ILogger logger, string system, string reason);
}
