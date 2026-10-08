using AiFramework.Infrastructure.ExternalSystems.Auth;
using AiFramework.Infrastructure.ExternalSystems.Certificates;
using AiFramework.Infrastructure.ExternalSystems.Health;
using AiFramework.Infrastructure.ExternalSystems.Http;
using Duende.AccessTokenManagement;
using Duende.IdentityModel.Client;
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
        var tokenManagement = AddTokenManagement(services, snapshot);

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

            if (tokenManagement is not null && system.Auth.Kind != ExternalSystemAuthKind.None)
            {
                AddTokenClient(services, tokenManagement, name, system);
                health.Add(new HealthCheckRegistration(
                    ExternalSystemNames.TokenCheck(name),
                    sp => ActivatorUtilities.CreateInstance<TokenHealthCheck>(sp, name),
                    failureStatus: null,
                    tags));
            }
        }

        return new ExternalSystemsBuilder(services, snapshot);
    }

    /// <summary>
    /// Duende's token management, registered once for every system that authenticates — or not
    /// at all when none does. Null: no system needs a token.
    /// </summary>
    private static ClientCredentialsTokenManagementBuilder? AddTokenManagement(
        IServiceCollection services, ExternalSystemsOptions snapshot)
    {
        if (snapshot.Systems.Values.All(system => system.Auth.Kind == ExternalSystemAuthKind.None))
        {
            return null;
        }

        var builder = services.AddClientCredentialsTokenManagement();

        // After Duende's own registration, which adds a no-op assertion service: the last
        // registration of a service type is the one resolved, so this one replaces it.
        services.AddSingleton<IClientAssertionService, PrivateKeyJwtAssertionService>();
        return builder;
    }

    private static void AddTokenClient(
        IServiceCollection services,
        ClientCredentialsTokenManagementBuilder tokenManagement,
        string name,
        ExternalSystemOptions system)
    {
        // The token endpoint presents the same client certificate as the system's own calls, for
        // Keycloak's X.509 authenticator and RFC 8705 certificate-bound tokens.
        services.AddHttpClient(ExternalSystemNames.TokenBackchannel(name))
            .ConfigurePrimaryHttpMessageHandler(sp =>
                sp.GetRequiredService<ExternalSystemHandlerFactory>().CreateTokenEndpointHandler(name));

        tokenManagement.AddClient(ClientCredentialsClientName.Parse(name), client =>
        {
            // Runs when the options are first resolved, not now: the secret file is read
            // lazily, so a missing file is that system's token failure, not a startup crash.
            // A ROTATED secret needs a restart — VSO's rolloutRestartTargets provides it.
            client.TokenEndpoint = new Uri(system.Auth.TokenEndpoint!); // validated absolute for Kind != None.
            client.ClientId = ClientId.Parse(system.Auth.ClientId!); // validated non-empty for Kind != None.
            client.HttpClientName = ExternalSystemNames.TokenBackchannel(name);
            client.ClientCredentialStyle = system.Auth.CredentialStyle == ExternalSystemCredentialStyle.PostBody
                ? ClientCredentialStyle.PostBody
                : ClientCredentialStyle.AuthorizationHeader;
            if (!string.IsNullOrWhiteSpace(system.Auth.Scope))
            {
                client.Scope = Scope.Parse(system.Auth.Scope);
            }

            if (system.Auth.Kind == ExternalSystemAuthKind.ClientSecret
                && TryReadSecret(system.Auth.ClientSecretFile!) is { } secret) // validated for ClientSecret.
            {
                client.ClientSecret = ClientSecret.Parse(secret);
            }
        });
    }

    // A missing secret leaves ClientSecret unset: the IdP answers 401 and the token check
    // reports it. Throwing from inside an options callback would fail the first CALL instead.
    private static string? TryReadSecret(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
