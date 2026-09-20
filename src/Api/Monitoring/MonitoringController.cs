using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// The operator-facing surface. Phase 1 of
/// docs/superpowers/plans/2026-09-20-monitoring-page.md establishes only the gate; job runs,
/// traffic and sign-in history arrive in phases 2 to 4 as further endpoints on this controller.
/// </summary>
/// <remarks>
/// The policy sits on the CONTROLLER, not on each action, so an endpoint added later is gated by
/// default rather than by remembering. A member reaching here gets 403 rather than 404: unlike an
/// order id (ADR 0007) or a username (ADR 0006), this route leaks nothing by admitting it exists
/// — its path is a fixed string compiled into the SPA bundle every user downloads. See ADR 0020.
/// </remarks>
[ApiController]
[Route("api/monitoring")]
[Authorize(Policy = AuthorizationPolicies.Monitoring)]
public sealed class MonitoringController(
    IQueryDispatcher queries, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Confirms monitoring access and identifies the operator.</summary>
    [HttpGet("access")]
    [ProducesResponseType<MonitoringAccessResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Access(CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return Unauthorized();
        }

        var result = await queries.SendAsync(new GetUser(userId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new MonitoringAccessResponse
            {
                UserId = result.Value.UserId,
                Username = result.Value.Username,
                Role = result.Value.Role,
            })
            : result.Problem(HttpContext);
    }
}
