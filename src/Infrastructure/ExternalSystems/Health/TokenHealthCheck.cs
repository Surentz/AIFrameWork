using Duende.AccessTokenManagement;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.ExternalSystems.Health;

/// <summary>
/// Can this system's token be obtained. Reads through Duende's cache, so a live token costs the
/// IdP nothing; an expired one is fetched exactly as a real call would. The failure description
/// reaches the browser (spec §3), so it is never a secret, an assertion, a token or a host name:
/// the IdP's OAuth error code (or HTTP reason phrase), or a fixed "token endpoint unreachable".
/// </summary>
internal sealed class TokenHealthCheck(IClientCredentialsTokenManager tokens, string systemName) : IHealthCheck
{
    private const int MaxErrorLength = 64;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = await tokens
            .GetAccessTokenAsync(ClientCredentialsClientName.Parse(systemName), ct: cancellationToken)
            .ConfigureAwait(false);

        return result.WasSuccessful(out _, out var failure)
            ? HealthCheckResult.Healthy($"{systemName} token available")
            : HealthCheckResult.Unhealthy($"{systemName} token unavailable: {Describe(failure.Error)}");
    }

    /// <summary>
    /// Duende 4.2's FailedResult.Error is IdentityModel's TokenResponse.Error, and FailedResult
    /// drops which kind it was: the OAuth "error" code for a 400, the HTTP reason phrase for any
    /// other failing status, and the exception's MESSAGE for a transport failure, which names the
    /// host and port ("... actively refused it. (idp.internal:8443)"). A code or a reason phrase
    /// is letters, digits, '_', '-' and spaces; a message carrying an endpoint never is.
    /// </summary>
    private static string Describe(string error) =>
        error.Length is > 0 and <= MaxErrorLength
        && error.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ' ')
            ? error
            : "token endpoint unreachable";
}
