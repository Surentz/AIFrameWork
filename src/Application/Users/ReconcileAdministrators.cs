using AiFramework.Application.Abstractions;
using AiFramework.Domain.Users;

namespace AiFramework.Application.Users;

/// <summary>
/// What a reconcile did, so the caller can log something worth reading.
/// </summary>
/// <param name="Promoted">Users moved to <see cref="UserRole.Admin"/>.</param>
/// <param name="Demoted">Users moved off it because configuration no longer lists them.</param>
/// <param name="Unknown">
/// Configured usernames matching no account. Not an error — an administrator may be configured
/// before they register — but far more often a typo, and a typo here fails silently in the
/// direction of nobody having access. Worth surfacing rather than swallowing.
/// </param>
public sealed record AdministratorReconciliation(
    int Promoted, int Demoted, IReadOnlyList<string> Unknown);

/// <summary>
/// Makes the stored roles match the configured administrator list: promote everyone named,
/// demote every administrator who is not. Idempotent — a run against an unchanged list reports
/// zero of each and writes nothing at all, which is what makes it safe to run at every API
/// start, in both replicas. See ADR 0020.
/// </summary>
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

        var promoted = 0;
        var demoted = 0;

        foreach (var user in candidates)
        {
            var target = wanted.Contains(user.UsernameNormalized) ? UserRole.Admin : UserRole.Member;

            // ChangeRole reports whether it actually moved, which is what keeps an unchanged
            // configuration from dirtying a single entity and so from issuing a single UPDATE.
            if (!user.ChangeRole(target))
            {
                continue;
            }

            if (target is UserRole.Admin)
            {
                promoted++;
            }
            else
            {
                demoted++;
            }
        }

        var found = candidates.Select(u => u.UsernameNormalized).ToHashSet(StringComparer.Ordinal);

        return Result.Success(new AdministratorReconciliation(
            promoted, demoted, [.. wanted.Where(name => !found.Contains(name))]));
    }
}
