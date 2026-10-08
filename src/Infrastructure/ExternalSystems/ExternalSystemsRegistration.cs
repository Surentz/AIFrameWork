using AiFramework.Infrastructure.ExternalSystems.Certificates;
using AiFramework.Infrastructure.ExternalSystems.Health;
using AiFramework.Infrastructure.ExternalSystems.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
        services.AddSingleton<ExternalSystemHandlerFactory>();

        var snapshot = section.Get<ExternalSystemsOptions>() ?? new ExternalSystemsOptions();

        var health = services.AddHealthChecks();
        foreach (var (name, system) in snapshot.Systems)
        {
            // Certificate, no token, no retry, no traffic: a probe is not an integration call.
            services.AddHttpClient(ExternalSystemNames.Probe(name), client =>
                {
                    client.BaseAddress = new Uri($"{system.BaseAddress.TrimEnd('/')}/");
                    client.Timeout = Timeout.InfiniteTimeSpan; // ProbeHealthCheck owns the timeout.
                })
                .ConfigurePrimaryHttpMessageHandler(sp =>
                    sp.GetRequiredService<ExternalSystemHandlerFactory>().CreatePrimaryHandler(name));

            string[] tags = [ExternalSystemHealth.Tag, name];
            health.Add(new HealthCheckRegistration(
                ExternalSystemNames.ProbeCheck(name),
                sp => ActivatorUtilities.CreateInstance<ProbeHealthCheck>(sp, name),
                failureStatus: null,
                tags));

            if (system.ClientCertificate is not null)
            {
                health.Add(new HealthCheckRegistration(
                    ExternalSystemNames.CertificateCheck(name),
                    sp => ActivatorUtilities.CreateInstance<CertificateHealthCheck>(sp, name),
                    failureStatus: null,
                    tags));
            }
        }

        return new ExternalSystemsBuilder(services, snapshot);
    }
}
