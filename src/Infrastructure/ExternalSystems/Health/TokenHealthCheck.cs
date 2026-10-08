using Duende.AccessTokenManagement;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.ExternalSystems.Health;

/// <summary>
/// Can this system's token be obtained. Reads through Duende's cache, so a live token costs the
/// IdP nothing; an expired one is fetched exactly as a real call would. The failure description
/// is Duende's error code only — never a secret, an assertion or a token.
/// </summary>
internal sealed class TokenHealthCheck(IClientCredentialsTokenManager tokens, string systemName) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = await tokens
            .GetAccessTokenAsync(ClientCredentialsClientName.Parse(systemName), ct: cancellationToken)
            .ConfigureAwait(false);

        return result.WasSuccessful(out _, out var failure)
            ? HealthCheckResult.Healthy($"{systemName} token available")
            : HealthCheckResult.Unhealthy($"{systemName} token unavailable: {failure.Error}");
    }
}
