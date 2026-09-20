using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// The monitoring area's own endpoint: who is looking at it. Each area of the page gets its own
/// controller beneath <c>api/monitoring</c> — jobs here already, traffic and sign-in history in
/// phases 3 and 4 — rather than one controller accumulating every operator concern.
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
