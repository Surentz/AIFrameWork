using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// Returned by AddExternalSystems. Carries the registration-time binding, because what to
/// register — a token handler, a certificate check — depends on configuration that has to be
/// known before the container exists (the same reason the worker reads Jobs off configuration).
/// </summary>
public sealed class ExternalSystemsBuilder
{
    internal ExternalSystemsBuilder(IServiceCollection services, ExternalSystemsOptions snapshot)
    {
        Services = services;
        Snapshot = snapshot;
    }

    public IServiceCollection Services { get; }

    internal ExternalSystemsOptions Snapshot { get; }
}
