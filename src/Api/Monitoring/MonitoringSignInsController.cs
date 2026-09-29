using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// Who signed in, who failed, who is locked out, and who is about.
/// </summary>
/// <remarks>
/// One controller per resource beneath <c>api/monitoring</c>, as the jobs area already does. The
/// policy is on the controller so an endpoint added later is gated by default. See ADR 0020.
/// </remarks>
[ApiController]
[Route("api/monitoring/sign-ins")]
[Authorize(Policy = AuthorizationPolicies.Monitoring.Read)]
public sealed class MonitoringSignInsController(IQueryDispatcher queries) : ControllerBase
{
    /// <summary>Counts by outcome, the locked-out accounts, and the active-user count.</summary>
    [HttpGet("health")]
    [ProducesResponseType<SignInHealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Health(
        [FromQuery] int windowHours = 24,
        [FromQuery] int activeWindowMinutes = 15,
        CancellationToken cancellationToken = default)
    {
        var result = await queries
            .SendAsync(
                new GetSignInHealth(
                    TimeSpan.FromHours(windowHours), TimeSpan.FromMinutes(activeWindowMinutes)),
                cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new SignInHealthResponse
            {
                Succeeded = result.Value.Succeeded,
                BadCredentials = result.Value.BadCredentials,
                LockedOut = result.Value.LockedOut,
                UnknownUser = result.Value.UnknownUser,
                Since = result.Value.Since,
                ActiveUsers = result.Value.Active.Count,
                ActiveWindowMinutes = (int)result.Value.Active.Window.TotalMinutes,
                LockedOutUsers =
                [
                    .. result.Value.LockedOutUsers.Select(user => new LockedOutUserResponse
                    {
                        UserId = user.UserId,
                        Username = user.Username,
                        LockedOutUntil = user.LockedOutUntil,
                    }),
                ],
            })
            : result.Problem(HttpContext);
    }

    /// <summary>The sign-in audit trail, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<SignInEventPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> List(
        [FromQuery] SignInOutcome? outcome = null,
        [FromQuery] string? username = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await queries
            .SendAsync(new GetSignInEvents(outcome, username, page, pageSize), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new SignInEventPageResponse
            {
                Items =
                [
                    .. result.Value.Items.Select(item => new SignInEventResponse
                    {
                        Id = item.Id,
                        At = item.At,
                        UserId = item.UserId,
                        UsernameAttempted = item.UsernameAttempted,
                        Outcome = item.Outcome,
                        IpAddress = item.IpAddress,
                        UserAgent = item.UserAgent,
                        TraceId = item.TraceId,
                    }),
                ],
                TotalCount = result.Value.TotalCount,
                Page = result.Value.Page,
            })
            : result.Problem(HttpContext);
    }
}
