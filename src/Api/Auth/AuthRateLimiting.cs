namespace AiFramework.Api.Auth;

/// <summary>
/// The rate-limiter policy guarding the credential endpoints. A named constant so Program.cs
/// and the controller cannot drift apart over a string literal.
/// </summary>
public static class AuthRateLimiting
{
    public const string PolicyName = "auth";
}
