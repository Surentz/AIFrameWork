using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Security;

public static class AdminRegistration
{
    /// <summary>
    /// Registers the administrator-list validator and the startup reconcile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The options themselves are bound by the host, not here — the same split
    /// <see cref="Caching.CacheOptions"/> and <c>JobOptions</c> use, so this method stays
    /// resolvable from a bare <see cref="IServiceCollection"/> with no IConfiguration behind it.
    /// A host that never binds "Admin" gets the defaults: no administrators, reconcile on.
    /// </para>
    /// <para>
    /// The extension exists so that <c>AdminOptionsValidator</c> and <c>AdminReconciler</c> can
    /// stay <c>internal</c>. <c>src/Api</c> composes this layer through methods like this one and
    /// contains no EF reference of its own; the reconciler has to name EF exception types to
    /// survive an absent database, so it belongs on this side of that line.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAdministratorRoles(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IValidateOptions<AdminOptions>, AdminOptionsValidator>();

        // ValidateOnStart so a typo in Admin__Usernames fails the deployment rather than quietly
        // leaving nobody with access. The reconcile itself never fails the host; bad
        // CONFIGURATION should, because nothing downstream can recover from it.
        services.AddOptions<AdminOptions>().ValidateOnStart();

        services.AddHostedService<AdminReconciler>();

        return services;
    }
}
