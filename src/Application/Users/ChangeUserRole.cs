using AiFramework.Application.Abstractions;
using AiFramework.Domain.Users;

namespace AiFramework.Application.Users;

/// <summary>
/// Promotes or demotes another account. See ADR 0022.
/// </summary>
/// <remarks>
/// <para>
/// <b>This does NOT rotate the target's security stamp, and must not.</b> The role is read from
/// the database on every authenticated request (ADR 0020), so a demotion takes effect on the
/// target's very next request with no window and no TTL — while rotating would sign them out of
/// a session they remain perfectly entitled to hold. Ending someone's sessions is
/// <see cref="SignOutUser"/>, a separate and deliberate act, never a side effect of this one.
/// </para>
/// <para>
/// <b>A demotion here is not the last word.</b> If the target is still named in
/// <c>Admin__Usernames</c>, the next API start promotes them again — configuration is a floor
/// (ADR 0022). Removing an administrator takes both steps, which is why <c>ListUsers</c> reports
/// whether an account's role is currently backed by configuration.
/// </para>
/// </remarks>
public sealed record ChangeUserRole(Guid UserId, UserRole Role) : ICommand<bool>;

public sealed class ChangeUserRoleHandler(
    IUserRepository users, ICurrentUser currentUser, IAdminAudit audit)
    : ICommandHandler<ChangeUserRole, bool>
{
    public async Task<Result<bool>> HandleAsync(
        ChangeUserRole command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The first rail. Read before anything else: the cheapest refusal, and the one whose
        // absence would let a single click remove the clicker's own access.
        if (currentUser.Id == command.UserId)
        {
            return Result.Failure<bool>(new Error(
                ErrorKind.Conflict,
                "user.cannot_change_own_role",
                "You cannot change your own role. Ask another administrator."));
        }

        var target = await users.GetAsync(command.UserId, cancellationToken).ConfigureAwait(false);

        if (target is null)
        {
            return Result.Failure<bool>(new Error(
                ErrorKind.NotFound, "user.not_found", "That account does not exist."));
        }

        // Nothing to do, and nothing to audit. Reported as success rather than as a conflict: the
        // caller asked for a state the system is already in, which is not an error, and a screen
        // that retries a click must not accumulate audit rows saying a role changed when it did not.
        if (target.Role == command.Role)
        {
            return Result.Success(false);
        }

        if (!await ApplyAsync(target, command.Role, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<bool>(new Error(
                ErrorKind.Conflict,
                "user.last_administrator",
                "That is the only administrator left. Promote somebody else first."));
        }

        await audit
            .RecordAsync(
                command.Role is UserRole.Admin ? AdminActionKind.Promoted : AdminActionKind.Demoted,
                new AdministeredUser(target.Id, target.Username),
                cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(true);
    }

    /// <summary>
    /// Moves the role, and reports false only when the last-administrator rail refused it.
    /// </summary>
    /// <remarks>
    /// The two directions take different paths on purpose. A demotion goes through
    /// <c>TryDemoteAsync</c>, which owns a transaction of its own — its rail needs both a
    /// conditional statement and an advisory lock, for the reason that port documents — so it
    /// lands immediately rather than in the caller's unit of work. A promotion has no rail, since
    /// it can never empty the administrator set, so it goes through the tracked entity and
    /// commits in the same transaction as its audit row.
    /// </remarks>
    private async Task<bool> ApplyAsync(User target, UserRole role, CancellationToken cancellationToken)
    {
        if (role is not UserRole.Member)
        {
            target.ChangeRole(role);
            return true;
        }

        return await users.TryDemoteAsync(target.Id, cancellationToken).ConfigureAwait(false);
    }
}
