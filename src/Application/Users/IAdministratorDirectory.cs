namespace AiFramework.Application.Users;

/// <summary>
/// Whether configuration appoints a given username as an administrator.
/// </summary>
/// <remarks>
/// <para>
/// The same list <c>ReconcileAdministrators</c> is handed at startup, asked one name at a time.
/// It exists because registration needs the answer too: ADR 0020 names the gap in its own
/// reconciler warning — "they hold no access until they register" — and on a fresh deployment
/// that means the operator registers, sees nothing, and holds no access until someone restarts
/// the API. Consulting the same authority at registration closes that without moving it: the
/// database still never decides, configuration does.
/// </para>
/// <para>
/// It grants nothing a restart would not have granted a moment later, so it widens no access.
/// A name in the list is claimable by whoever registers it first either way.
/// </para>
/// </remarks>
public interface IAdministratorDirectory
{
    /// <summary>
    /// True when <paramref name="username"/> matches a configured administrator, compared through
    /// <c>User.Normalize</c> so that "Ada" and "ada" name the same account.
    /// </summary>
    public bool IsAdministrator(string username);
}
