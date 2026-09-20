using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Security;

/// <summary>
/// <see cref="IAdministratorDirectory"/> over <see cref="AdminOptions"/> — the same configured
/// list <c>AdminReconciler</c> applies at startup. See ADR 0020.
/// </summary>
/// <remarks>
/// Registered in <c>AddInfrastructure</c> rather than in <c>AddAdministratorRoles</c>, which only
/// the API calls: <c>RegisterUserHandler</c> depends on this port, <c>AddMessaging</c> registers
/// that handler in every host, and the generic host validates every descriptor it can construct.
/// Registering it beside the reconciler would fail the worker's container build — the same way an
/// unregistered <c>IClientContext</c> already did once.
/// </remarks>
internal sealed class AdministratorDirectory(IOptions<AdminOptions> options)
    : IAdministratorDirectory
{
    public bool IsAdministrator(string username)
    {
        ArgumentNullException.ThrowIfNull(username);

        var wanted = User.Normalize(username);

        // A blank entry cannot match an account and fails startup validation in any host that
        // binds the section; skipped here so a host that does not bind it cannot throw out of
        // User.Normalize on one.
        return options.Value.Usernames.Any(configured =>
            !string.IsNullOrWhiteSpace(configured)
            && string.Equals(User.Normalize(configured), wanted, StringComparison.Ordinal));
    }
}
