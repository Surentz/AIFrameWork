using System.Security.Claims;
using AiFramework.Application.Abstractions;

namespace AiFramework.Api.Auth;

/// <summary>
/// The caller, read from the cookie's claims. The only place NameIdentifier is parsed —
/// AuthController used to do it privately, and a second parser is a second thing to get wrong.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? Id =>
        Guid.TryParse(
            accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;
}
