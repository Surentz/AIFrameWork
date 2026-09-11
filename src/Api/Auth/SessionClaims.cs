namespace AiFramework.Api.Auth;

/// <summary>
/// Claim types this application mints itself. Named in one place because the value is written in
/// AuthController and read in Program.cs's OnValidatePrincipal, and a typo across those two
/// would fail OPEN in the worst way - the validator would find no stamp, and Task 5's guard
/// would reject every session rather than none, which at least fails loudly. Keeping one
/// constant removes the question.
/// </summary>
public static class SessionClaims
{
    /// <summary>The security stamp the session was issued under. See ADR 0011.</summary>
    public const string SecurityStamp = "aiframework:stamp";
}
