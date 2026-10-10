using AiFramework.Infrastructure.ExternalSystems.Health;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.ExternalSystems;

/// <summary>
/// Shape only, at startup. Files are NOT checked here: a missing certificate is one system's
/// runtime health failure, never a host that refuses to start (spec §2).
/// </summary>
internal sealed class ExternalSystemsOptionsValidator : IValidateOptions<ExternalSystemsOptions>
{
    public ValidateOptionsResult Validate(string? name, ExternalSystemsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        foreach (var (systemName, system) in options.Systems)
        {
            Check(systemName, system, failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(string name, ExternalSystemOptions system, List<string> failures)
    {
        var at = $"ExternalSystems:Systems:{name}";

        if (string.Equals(name, ExternalSystemHealth.Tag, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{at}: the system name must not be \"{ExternalSystemHealth.Tag}\" (any case): it is the tag that marks external health checks, so the system would never get a status row.");
        }

        // The name ends up in health-check names, metric labels and a traffic row's Name, and
        // ":" is the separator in the first of those.
        if (name.Length is 0 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            failures.Add($"{at}: the system name must be 1-64 ASCII letters, digits or dashes.");
        }

        if (!IsAbsoluteHttp(system.BaseAddress))
        {
            failures.Add($"{at}:BaseAddress must be an absolute http or https URI.");
        }

        if (system.Probe.Method is not ("GET" or "HEAD"))
        {
            failures.Add($"{at}:Probe:Method must be GET or HEAD.");
        }

        if (system.Probe.Timeout <= TimeSpan.Zero)
        {
            failures.Add($"{at}:Probe:Timeout must be positive.");
        }

        CheckResilience(at, system.Resilience, failures);
        CheckAuth(at, system, failures);

        if (system.ClientCertificate is { Path.Length: 0 })
        {
            failures.Add($"{at}:ClientCertificate:Path is required when ClientCertificate is configured.");
        }

        if (system.ServerTrust is { CaBundlePath.Length: 0 })
        {
            failures.Add($"{at}:ServerTrust:CaBundlePath is required when ServerTrust is configured.");
        }

        if (system.CertificateExpiryWarning <= TimeSpan.Zero)
        {
            failures.Add($"{at}:CertificateExpiryWarning must be positive.");
        }
    }

    private static void CheckResilience(string at, ExternalSystemResilienceOptions resilience, List<string> failures)
    {
        if (resilience.TotalRequestTimeout <= TimeSpan.Zero || resilience.AttemptTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{at}:Resilience timeouts must be positive.");
        }

        // The standard handler would otherwise throw while BUILDING the pipeline, naming neither
        // property — the green-build, dead-host shape ADR 0014 records.
        if (resilience.AttemptTimeout > resilience.TotalRequestTimeout)
        {
            failures.Add($"{at}:Resilience:AttemptTimeout must not exceed TotalRequestTimeout.");
        }

        if (resilience.MaxRetryAttempts < 1)
        {
            failures.Add(
                $"{at}:Resilience:MaxRetryAttempts must be at least 1: Polly rejects zero. A client " +
                "that must not retry is registered with WithoutRetry(reason).");
        }

        if (resilience.BaseDelay <= TimeSpan.Zero)
        {
            failures.Add($"{at}:Resilience:BaseDelay must be positive.");
        }
    }

    private static void CheckAuth(string at, ExternalSystemOptions system, List<string> failures)
    {
        var auth = system.Auth;
        if (auth.Kind == ExternalSystemAuthKind.None)
        {
            return;
        }

        if (!IsAbsoluteHttp(auth.TokenEndpoint))
        {
            failures.Add($"{at}:Auth:TokenEndpoint must be an absolute http or https URI.");
        }

        if (string.IsNullOrWhiteSpace(auth.ClientId))
        {
            failures.Add($"{at}:Auth:ClientId is required.");
        }

        if (auth.Kind == ExternalSystemAuthKind.ClientSecret && string.IsNullOrWhiteSpace(auth.ClientSecretFile))
        {
            failures.Add($"{at}:Auth:ClientSecretFile is required for ClientSecret.");
        }

        if (auth.Kind == ExternalSystemAuthKind.PrivateKeyJwt)
        {
            if (system.ClientCertificate is null)
            {
                failures.Add($"{at}:ClientCertificate is required for PrivateKeyJwt: it signs the assertion.");
            }

            if (!IsAbsoluteHttp(auth.Issuer))
            {
                failures.Add($"{at}:Auth:Issuer must be the authorization server's absolute issuer URL for PrivateKeyJwt.");
            }
        }
    }

    // Scheme, not merely "absolute": on Linux "/rates" parses as an absolute file:// URI.
    // ResilienceRegistration records the same trap.
    private static bool IsAbsoluteHttp(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal));
}
