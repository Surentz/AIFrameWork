using AiFramework.Domain.Users;

namespace AiFramework.Application.Users;

/// <summary>
/// Who the caller is, as the Api layer needs it. Deliberately carries no password material.
/// Shared by register, sign-in, change-password and the current-user query rather than declared
/// four times.
/// <para>
/// <c>SecurityStamp</c> is consumed only by the code that mints the cookie. It is never mapped
/// into <c>SessionResponse</c> and must not be: it is a session-invalidation token, not profile
/// data, and publishing it would let a client hold the value that proves a session current.
/// </para>
/// <para>
/// <c>Role</c> is the opposite: it IS published, because the SPA needs it to decide whether to
/// render the monitoring nav entry. That is cosmetics, not the control — authorization is the
/// policy on the server, which reads the role from the database rather than from anything the
/// client was told. See ADR 0020.
/// </para>
/// </summary>
public sealed record SessionView(
    Guid UserId, string Username, string DisplayName, string SecurityStamp, UserRole Role);

/// <summary>
/// Length only, no composition rules — current NIST guidance is that forcing a digit and a
/// symbol buys less than length does, and drives people towards predictable substitutions.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;
}
