using System.Globalization;
using System.Security.Claims;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AiFramework.Api.Auth;

[ApiController]
[Route("api/auth")]
[Authorize]
public sealed class AuthController(
    ICommandDispatcher commands, IQueryDispatcher queries, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Creates an account and signs it in.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimiting.PolicyName)]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
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
    [EnableRateLimiting(AuthRateLimiting.PolicyName)]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
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
        if (currentUser.Id is not { } userId)
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

        if (currentUser.Id is not { } userId)
        {
            return Unauthorized();
        }

        var result = await commands.SendAsync(
            new ChangePassword(userId, request.CurrentPassword, request.NewPassword),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return result.Problem(HttpContext);
        }

        // Read the current cookie's persistence before replacing it. Re-issuing with a hard-coded
        // false would silently downgrade a "remember me" session to a browser-session cookie as a
        // side effect of changing a password.
        var existing = await HttpContext
            .AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme)
            .ConfigureAwait(false);

        // The stamp this request's cookie carries is now stale - the handler rotated it. Without
        // re-issuing, this caller's very next request would 401 (Program.cs's OnValidatePrincipal),
        // which is not what rotating the stamp is for: it is meant to end OTHER sessions.
        //
        // IssueCookieAsync builds a fresh AuthenticationProperties, so the cookie's absolute expiry
        // restarts from now rather than carrying over the original sign-in's. Benign: Program.cs has
        // SlidingExpiration on, so an active session's expiry is already being pushed forward on every
        // request. Only IsPersistent is deliberately carried over from the cookie being replaced.
        await IssueCookieAsync(result.Value, existing.Properties?.IsPersistent ?? false)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Invalidates every session for the signed-in user, including this one.</summary>
    [HttpPost("sign-out-everywhere")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> SignOutEverywhere(CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return Unauthorized();
        }

        var result = await commands.SendAsync(
            new Application.Users.SignOutEverywhere(userId), cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return result.Problem(HttpContext);
        }

        // Clears this browser's cookie as well. The rotation alone would already make it fail
        // validation on the next request, so this is tidiness rather than the security boundary -
        // it avoids one guaranteed 401 round trip before the SPA notices.
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
            .ConfigureAwait(false);

        return NoContent();
    }

    private static SessionResponse ToResponse(SessionView session) => new()
    {
        UserId = session.UserId,
        Username = session.Username,
        DisplayName = session.DisplayName,
    };

    /// <summary>
    /// The only place a session is minted. The claims are the whole session: NameIdentifier is what
    /// <see cref="ICurrentUser.Id"/> reads back on later requests, and the security stamp is what
    /// Program.cs's OnValidatePrincipal compares against the database on every request. Nothing else
    /// has to be looked up to know who is calling.
    /// </summary>
    private Task IssueCookieAsync(SessionView session, bool persistent)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(
                    ClaimTypes.NameIdentifier,
                    session.UserId.ToString("D", CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, session.Username),
                new Claim(SessionClaims.SecurityStamp, session.SecurityStamp),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = persistent });
    }
}
