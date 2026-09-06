namespace AiFramework.Application.Users;

/// <summary>
/// Who the caller is, as the Api layer needs it. Deliberately carries no password material.
/// Shared by register, sign-in and the current-user query rather than declared three times.
/// </summary>
public sealed record SessionView(Guid UserId, string Username, string DisplayName);

/// <summary>
/// Length only, no composition rules — current NIST guidance is that forcing a digit and a
/// symbol buys less than length does, and drives people towards predictable substitutions.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;
}
