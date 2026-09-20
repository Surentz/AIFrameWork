using AiFramework.Application.Abstractions;
using AiFramework.Domain.Users;

namespace AiFramework.Application.Users;

/// <summary>
/// What a reconcile did, so the caller can log something worth reading.
/// </summary>
/// <param name="Promoted">
/// Usernames moved to <see cref="UserRole.Admin"/> by this run — the names themselves rather than
/// a count, because "why is this person an administrator again" is the question configuration
/// being a floor invites, and a count cannot answer it. See ADR 0022.
/// </param>
/// <param name="Unknown">
/// Configured usernames matching no account. Not an error — an administrator may be configured
/// before they register, and <c>RegisterUser</c> promotes them the moment they do — but far more
/// often a typo, and a typo here fails silently in the direction of nobody having access. Worth
/// surfacing rather than swallowing.
/// </param>
public sealed record AdministratorReconciliation(
    IReadOnlyList<string> Promoted, IReadOnlyList<string> Unknown);

/// <summary>
/// Promotes every configured username that is not already an administrator, and demotes nobody.
/// </summary>
/// <remarks>
/// <para>
/// <b>Configuration is a floor, not a mirror.</b> ADR 0020 had this demote every administrator
/// absent from the list, which made the configured set the whole truth; ADR 0022 supersedes that,
/// because a list that demotes cannot coexist with a screen that grants — every in-app change
/// would be undone at the next start, silently and with nothing in the logs naming the cause.
/// </para>
/// <para>
/// The consequence is a sharp edge and it belongs in the reader's mind here rather than only in
/// the ADR: <b>removing a name from configuration now revokes nothing.</b> Revocation is an
/// in-app action, and an account demoted in the app while still named here is promoted straight
/// back at the next start. Removing an administrator takes both steps.
/// </para>
/// <para>
/// Still idempotent, which is what keeps it safe to run at every API start in both replicas: a
/// run against a list whose members already hold the role dirties no entity and issues no UPDATE.
/// </para>
/// </remarks>
public sealed record ReconcileAdministrators(IReadOnlyList<string> Usernames)
    : ICommand<AdministratorReconciliation>;

public sealed class ReconcileAdministratorsHandler(IUserRepository users)
    : ICommandHandler<ReconcileAdministrators, AdministratorReconciliation>
{
    public async Task<Result<AdministratorReconciliation>> HandleAsync(
        ReconcileAdministrators command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Normalize here, once, rather than at each comparison: User.Normalize is the only lookup
        // key this application has, and a configured "Ada" must reach the row stored as "ADA".
        var wanted = command.Usernames
            .Select(User.Normalize)
            .ToHashSet(StringComparer.Ordinal);

        var candidates = await users
            .ListForRoleReconciliationAsync([.. wanted], cancellationToken)
            .ConfigureAwait(false);

        var promoted = new List<string>();

        // S3267 would have this as candidates.Where(u => u.ChangeRole(...)).Select(...), which
        // puts a MUTATION inside a filter: promotion would then be a side effect of a predicate,
        // happening only if and when something materialises the sequence, and a later .Take() or
        // reordering would silently promote nobody. The condition here reads as a filter but is
        // not one, so the loop stays.
#pragma warning disable S3267
        foreach (var user in candidates)
        {
            // ChangeRole reports whether it actually moved, which is what keeps an unchanged
            // configuration from dirtying a single entity and so from issuing a single UPDATE.
            // Nobody is moved the other way: an administrator this list does not name is left
            // exactly as they are, because something else may have granted it deliberately.
            if (user.ChangeRole(UserRole.Admin))
            {
                promoted.Add(user.Username);
            }
        }
#pragma warning restore S3267

        var found = candidates.Select(u => u.UsernameNormalized).ToHashSet(StringComparer.Ordinal);

        return Result.Success(new AdministratorReconciliation(
            promoted, [.. wanted.Where(name => !found.Contains(name))]));
    }
}
