using AiFramework.Api.Auth;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// Administering accounts: who holds the administrator role, and revoking someone's sessions.
/// </summary>
/// <remarks>
/// <para>
/// Beneath <c>api/monitoring</c>, because administering users is an operations task, but behind
/// <see cref="AuthorizationPolicies.Users.Manage"/> rather than the monitoring read policy: who may
/// grant the administrator role is a different capability from who may look at a dashboard, even
/// while one role holds both. See ADR 0020 for the role, ADR 0022 for this feature, and ADR 0024
/// for the capability names.
/// </para>
/// <para>
/// Both rails — no self-demotion, never the last administrator — are enforced in the handlers
/// and surface here as <c>409 Conflict</c>. They are rules, not UI affordances: a screen that
/// hides the buttons is an explanation, and this is the enforcement.
/// </para>
/// </remarks>
[ApiController]
[Route("api/monitoring/users")]
[Authorize(Policy = AuthorizationPolicies.Users.Manage)]
// One route template per action below, so S6960's "controller with multiple responsibilities"
// heuristic does not fire on a controller that has exactly one: administering accounts.
#pragma warning disable S6960
public sealed class MonitoringUsersController(
    IQueryDispatcher queries, ICommandDispatcher commands) : ControllerBase
#pragma warning restore S6960
{
    /// <summary>The accounts, most recently seen first.</summary>
    [HttpGet]
    [ProducesResponseType<AdministeredUserPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> List(
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await queries
            .SendAsync(new ListUsers(search, page, pageSize), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new AdministeredUserPageResponse
            {
                Items = [.. result.Value.Items.Select(Map)],
                TotalCount = result.Value.TotalCount,
                Page = result.Value.Page,
            })
            : result.Problem(HttpContext);
    }

    /// <summary>Promotes or demotes an account.</summary>
    /// <remarks>
    /// Does NOT end the target's sessions, deliberately: the role is read from the database on
    /// every request, so a demotion takes effect on their next one without signing them out of a
    /// session they still hold legitimately. Use <c>sign-out</c> below for that.
    /// </remarks>
    [HttpPost("{id:guid}/role")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> ChangeRole(
        Guid id,
        [FromBody] ChangeUserRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await commands
            .SendAsync(new ChangeUserRole(id, request.Role), cancellationToken)
            .ConfigureAwait(false);

        // 204 whether or not the role actually moved. The handler reports false when the account
        // already held the requested role, which is the caller asking for a state the system is
        // already in - not an error, and not worth a distinct status a client would have to
        // branch on.
        return result.IsSuccess ? NoContent() : result.Problem(HttpContext);
    }

    /// <summary>Revokes every session the account holds, by rotating its security stamp.</summary>
    [HttpPost("{id:guid}/sign-out")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> SignOut(
        Guid id, CancellationToken cancellationToken = default)
    {
        var result = await commands
            .SendAsync(new SignOutUser(id), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : result.Problem(HttpContext);
    }

    /// <summary>What has been done to one account, newest first.</summary>
    [HttpGet("{id:guid}/actions")]
    [ProducesResponseType<AdminActionPageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Actions(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await queries
            .SendAsync(new GetUserActions(id, page, pageSize), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? Ok(new AdminActionPageResponse
            {
                Items = [.. result.Value.Items.Select(Map)],
                TotalCount = result.Value.TotalCount,
                Page = result.Value.Page,
            })
            : result.Problem(HttpContext);
    }

    private static AdministeredUserResponse Map(AdministeredUserView user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        DisplayName = user.DisplayName,
        Role = user.Role,
        RoleIsConfigured = user.RoleIsConfigured,
        RegisteredAt = user.RegisteredAt,
        LastSeenAt = user.LastSeenAt,
    };

    private static AdminActionResponse Map(AdminActionView action) => new()
    {
        Id = action.Id,
        At = action.At,
        Kind = action.Kind,
        ActorUserId = action.ActorUserId,
        ActorUsername = action.ActorUsername,
        TargetUserId = action.TargetUserId,
        TargetUsername = action.TargetUsername,
        IpAddress = action.IpAddress,
        TraceId = action.TraceId,
    };
}
