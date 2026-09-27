namespace AiFramework.Api.Auth;

/// <summary>
/// The application's authorization policies, named once because the name is written in
/// Program.cs's registration and read in every <c>[Authorize(Policy = ...)]</c> attribute, and a
/// typo across those two fails in the worse direction — ASP.NET Core throws at request time for
/// an unknown policy, which is at least loud, but only on the endpoint nobody tested.
/// </summary>
/// <remarks>
/// <para>
/// Named for the CAPABILITY an endpoint needs, never for the role that happens to grant it today.
/// Every policy here currently resolves to <see cref="Domain.Users.UserRole.Admin"/>, so the split
/// changes nobody's access. What it buys is ADR 0020's exit: when a permission model arrives,
/// <c>Role</c> becomes one input to a requirement handler behind these same names, and no
/// controller changes. A policy named <c>"Admin"</c> would have had to be renamed at every call
/// site to get there.
/// </para>
/// <para>
/// <see cref="All"/> is what Program.cs registers, so adding a constant without listing it there
/// fails <c>AuthorizationPolicyTests</c> rather than throwing at request time.
/// </para>
/// </remarks>
public static class AuthorizationPolicies
{
    /// <summary>Every policy name, registered together in Program.cs.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Orders.Fulfil,
        Catalogue.Manage,
        Monitoring.Read,
        Monitoring.Operate,
        Users.Manage,
    ];

    public static class Orders
    {
        /// <summary>Seeing every buyer's orders that await fulfilment, and shipping them.</summary>
        public const string Fulfil = "Orders.Fulfil";
    }

    public static class Catalogue
    {
        /// <summary>
        /// Creating and editing products. Defined but not yet applied: the catalogue is still
        /// writable by any signed-in caller (ADR 0013) until its own lock-down lands.
        /// </summary>
        public const string Manage = "Catalogue.Manage";
    }

    public static class Monitoring
    {
        /// <summary>Everything beneath <c>api/monitoring</c> that only inspects.</summary>
        public const string Read = "Monitoring.Read";

        /// <summary>
        /// The monitoring actions that start or replay work: retrying a dead letter, triggering a
        /// job. Applied per ACTION, on top of the controller's <see cref="Read"/>, so a caller
        /// needs both.
        /// </summary>
        public const string Operate = "Monitoring.Operate";
    }

    public static class Users
    {
        /// <summary>Granting and revoking the administrator role, and ending someone's sessions.</summary>
        public const string Manage = "Users.Manage";
    }
}
