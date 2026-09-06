using System.Globalization;
using System.Security.Claims;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.Auth;

[ApiController]
[Route("api/auth")]
[Authorize]
public sealed class AuthController(
    ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
{
    /// <summary>Creates an account and signs it in.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Register(
        RegisterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await commands.SendAsync(
            new RegisterUser(request.Username, request.Password, request.DisplayName),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return result.Problem(HttpContext);
        }

        await IssueCookieAsync(result.Value, persistent: false).ConfigureAwait(false);

        return Ok(ToResponse(result.Value));
    }

    /// <summary>Signs in with a username and password.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await commands.SendAsync(
            new SignIn(request.Username, request.Password), cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return result.Problem(HttpContext);
        }

        await IssueCookieAsync(result.Value, request.RememberMe).ConfigureAwait(false);

        return Ok(ToResponse(result.Value));
    }

    /// <summary>Signs out, clearing the session cookie.</summary>
    /// <remarks>
    /// Anonymous deliberately: signing out when the cookie has already expired is the caller
    /// getting what they asked for, not an error worth a 401.
    /// </remarks>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Returns the signed-in user.</summary>
    [HttpGet("me")]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> Me(CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var result = await queries.SendAsync(new GetUser(userId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? Ok(ToResponse(result.Value)) : result.Problem(HttpContext);
    }

    /// <summary>Changes the signed-in user's own password.</summary>
    [HttpPost("change-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> ChangeOwnPassword(
        ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (CurrentUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var result = await commands.SendAsync(
            new ChangePassword(userId, request.CurrentPassword, request.NewPassword),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? NoContent() : result.Problem(HttpContext);
    }

    private static SessionResponse ToResponse(SessionView session) => new()
    {
        UserId = session.UserId,
        Username = session.Username,
        DisplayName = session.DisplayName,
    };

    /// <summary>
    /// The only place a session is minted. The claims are the whole session: NameIdentifier is
    /// what <see cref="CurrentUserId"/> reads back on later requests, so nothing else has to be
    /// looked up to know who is calling.
    /// </summary>
    private Task IssueCookieAsync(SessionView session, bool persistent)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(
                    ClaimTypes.NameIdentifier,
                    session.UserId.ToString("D", CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, session.Username),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = persistent });
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
