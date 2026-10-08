using AiFramework.Infrastructure.ExternalSystems.Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.ExternalSystems;

public static class ExternalSystemsRegistration
{
    /// <summary>
    /// Every configured external system: options, validation, and — added by later tasks — the
    /// certificate provider, primary handlers, probe clients, health checks and token clients.
    /// Called by EACH host's Program.cs, not by AddInfrastructure, because the set of named
    /// clients must be known at registration time. ADR 0031.
    /// </summary>
    public static ExternalSystemsBuilder AddExternalSystems(
        this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        services.AddOptions<ExternalSystemsOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ExternalSystemsOptions>, ExternalSystemsOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICertificateProvider, FileCertificateProvider>();

        var snapshot = section.Get<ExternalSystemsOptions>() ?? new ExternalSystemsOptions();
        return new ExternalSystemsBuilder(services, snapshot);
    }
}
