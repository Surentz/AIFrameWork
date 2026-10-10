using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.ExternalSystems.Health;

/// <summary>
/// Is the system reachable — DNS, TCP and the mTLS handshake — with the client certificate but
/// WITHOUT a token, so a 401 is the expected healthy answer. Reading adopted from egdw_eghealth's
/// HttpProbeHealthCheck: below 500 Healthy, 5xx Unhealthy, own timeout Degraded. Descriptions
/// carry the status code or the error CATEGORY only — never a body, host name or port.
/// </summary>
internal sealed partial class ProbeHealthCheck(
    IHttpClientFactory clients,
    IOptionsMonitor<ExternalSystemsOptions> options,
    string systemName,
    ILogger<ProbeHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var probe = options.CurrentValue.Find(systemName)?.Probe ?? new ProbeOptions();
        var client = clients.CreateClient(ExternalSystemNames.Probe(systemName));
        using var request = new HttpRequestMessage(new HttpMethod(probe.Method), probe.Path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(probe.Timeout);

        try
        {
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var code = (int)response.StatusCode;

            return code >= 500
                ? HealthCheckResult.Unhealthy($"{systemName} answered {code}")
                : HealthCheckResult.Healthy($"{systemName} reachable ({code})");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded(
                $"{systemName} did not answer within {probe.Timeout.TotalSeconds:0.#} s");
        }
        catch (HttpRequestException exception)
        {
            LogUnreachable(exception, systemName, exception.HttpRequestError);
            return HealthCheckResult.Unhealthy($"{systemName} unreachable: {exception.HttpRequestError}");
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Probe of {System} failed: {Category}")]
    private partial void LogUnreachable(Exception exception, string system, HttpRequestError category);
}
