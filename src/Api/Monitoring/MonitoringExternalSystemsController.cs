using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// The external systems' health, as the worker last saw it, and their last hour of traffic.
/// Reads only: the checks themselves run in the worker (ADR 0032), never on this request.
/// </summary>
[ApiController]
[Route("api/monitoring/external-systems")]
[Authorize(Policy = AuthorizationPolicies.Monitoring.Read)]
public sealed class MonitoringExternalSystemsController(IQueryDispatcher queries) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ExternalSystemsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> List(CancellationToken cancellationToken)
    {
        var result = await queries.SendAsync(new GetExternalSystemStatus(), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new ExternalSystemsResponse
            {
                TrafficSince = result.Value.TrafficSince,
                Systems =
                [
                    .. result.Value.Systems.Select(row => new ExternalSystemRowResponse
                    {
                        Name = row.Name,
                        State = row.State,
                        Description = row.Description,
                        CheckedAt = row.CheckedAt,
                        Stale = row.Stale,
                        CertificateNotAfter = row.CertificateNotAfter,
                        TokenOk = row.TokenOk,
                        Calls = row.Calls,
                        Failed = row.Failed,
                        Faulted = row.Faulted,
                        Attempts = row.Attempts,
                        P95Ms = row.P95Ms,
                    }),
                ],
            })
            : result.Problem(HttpContext);
    }
}
