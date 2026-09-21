using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Users;

/// <summary>
/// Revokes every session another account holds, by rotating its security stamp (ADR 0011).
/// </summary>
/// <remarks>
/// <para>
/// A separate command from <see cref="SignOutEverywhere"/>, which is self-aimed, rather than one
/// with an optional target. The two differ in exactly the way that matters: one is a user
/// exercising control over their own sessions and needs no privilege, the other is an
/// administrator acting on somebody else's and needs the monitoring policy. Sharing a command
/// would mean sharing an authorization rule between them.
/// </para>
/// <para>
/// This is the deliberate act that <see cref="ChangeUserRole"/> deliberately is not. Demotion
/// takes effect on the next request without disturbing a session the user still legitimately
/// holds; this one ends every session they have, which is what "this person must stop having
/// access now" actually requires.
/// </para>
/// </remarks>
public sealed record SignOutUser(Guid UserId) : ICommand<bool>;

public sealed class SignOutUserHandler(
    IUserRepository users, ICurrentUser currentUser, IAdminAudit audit)
    : ICommandHandler<SignOutUser, bool>
{
    public async Task<Result<bool>> HandleAsync(
        SignOutUser command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Refused for the same reason self-demotion is: an administrator who ends their own
        // sessions from this screen is signed out mid-task with no warning, and SignOutEverywhere
        // on their own account is the endpoint that does it on purpose.
        if (currentUser.Id == command.UserId)
        {
            return Result.Failure<bool>(new Error(
                ErrorKind.Conflict,
                "user.cannot_sign_out_self_here",
                "Use sign-out-everywhere on your own account instead."));
        }

        var target = await users.GetAsync(command.UserId, cancellationToken).ConfigureAwait(false);

        if (target is null)
        {
            return Result.Failure<bool>(new Error(
                ErrorKind.NotFound, "user.not_found", "That account does not exist."));
        }

        target.RotateSecurityStamp();

        await audit
            .RecordAsync(
                AdminActionKind.SignedOutEverywhere,
                new AdministeredUser(target.Id, target.Username),
                cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(true);
    }
}
