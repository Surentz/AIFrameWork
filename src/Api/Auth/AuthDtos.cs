using AiFramework.Domain.Users;

namespace AiFramework.Api.Auth;

/// <summary>
/// Business-rule validation (length, charset, password policy) is owned by the validators in
/// <c>src/Application/Users</c>, not duplicated here as DataAnnotations. See <c>src/Api/CLAUDE.md</c>.
/// </summary>
public sealed record RegisterRequest
{
    public required string Username { get; init; }

    public required string Password { get; init; }

    public required string DisplayName { get; init; }
}

public sealed record LoginRequest
{
    public required string Username { get; init; }

    public required string Password { get; init; }

    /// <summary>
    /// Whether the session cookie outlives the browser session. Maps to the auth cookie's
    /// IsPersistent, which is the whole of what "remember me" means under cookie authentication.
    /// </summary>
    public required bool RememberMe { get; init; }
}

public sealed record ChangePasswordRequest
{
    public required string CurrentPassword { get; init; }

    public required string NewPassword { get; init; }
}

/// <summary>Who the caller is. Carries no password material, by construction.</summary>
public sealed record SessionResponse
{
    public required Guid UserId { get; init; }

    public required string Username { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// What the caller may do beyond their own data. Published so the SPA can decide whether to
    /// render the monitoring nav entry — cosmetics, not the control. Authorization is the policy
    /// on the server, which reads the role from the database on every request and never from
    /// anything the client was told. Crosses the wire as a NAME ("Admin"), via the
    /// JsonStringEnumConverter registered on both JsonOptions types in Program.cs. See ADR 0020.
    /// </summary>
    public required UserRole Role { get; init; }
}
