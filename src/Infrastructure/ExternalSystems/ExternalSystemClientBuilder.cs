using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>Per-client options, applied through named options so no terminal "Build()" can be forgotten.</summary>
public sealed class ExternalSystemClientBuilder<TApi>
    where TApi : class
{
    internal ExternalSystemClientBuilder(IServiceCollection services, string systemName)
    {
        Services = services;
        SystemName = systemName;
    }

    public IServiceCollection Services { get; }

    public string SystemName { get; }

    /// <summary>
    /// This client's calls are not idempotent and the partner takes no idempotency key: never
    /// retry. The reason is mandatory and is logged at startup. ADR 0014.
    /// </summary>
    public ExternalSystemClientBuilder<TApi> WithoutRetry(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Services.Configure<ExternalSystemClientSettings>(SystemName, s => s.RetryDisabledReason = reason);
        return this;
    }

    /// <summary>The Application port this partner implements, and its adapter.</summary>
    public ExternalSystemClientBuilder<TApi> WithAdapter<TPort, TAdapter>()
        where TPort : class
        where TAdapter : class, TPort
    {
        Services.AddScoped<TPort, TAdapter>();
        return this;
    }
}
