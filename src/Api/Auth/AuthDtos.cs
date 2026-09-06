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
}
