namespace AiFramework.Api.Auth;

/// <summary>
/// The application's authorization policies, named once because the name is written in
/// Program.cs's registration and read in every <c>[Authorize(Policy = ...)]</c> attribute, and a
/// typo across those two fails in the worse direction — ASP.NET Core throws at request time for
/// an unknown policy, which is at least loud, but only on the endpoint nobody tested.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Requires <see cref="Domain.Users.UserRole.Admin"/>. The whole of the monitoring page's
    /// access control; the SPA hiding its nav entry is cosmetics on top. See ADR 0020.
    /// </summary>
    public const string Monitoring = "Monitoring";
}
