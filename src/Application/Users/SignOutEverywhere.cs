using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Users;

/// <summary>
/// Invalidates every session for the caller, the one making the request included.
/// <c>bool</c> deliberately, unlike <see cref="ChangePassword"/>: this ends the acting session
/// too, so there is no cookie to re-issue and nothing worth returning. <see cref="Result{T}"/>
/// has no unit type.
/// </summary>
public sealed record SignOutEverywhere(Guid UserId) : ICommand<bool>;

public sealed class SignOutEverywhereHandler(IUserRepository users, ISignInAudit audit)
    : ICommandHandler<SignOutEverywhere, bool>
{
    public async Task<Result<bool>> HandleAsync(
        SignOutEverywhere command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var user = await users.GetAsync(command.UserId, cancellationToken).ConfigureAwait(false);

        // Unauthorized rather than NotFound, the same reasoning as ChangePasswordHandler: the id
        // came from the caller's own session, so a missing row means that session is stale.
        if (user is null)
        {
            return Result.Failure<bool>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        user.RotateSecurityStamp();

        // Audited alongside the sign-ins rather than separately: "every session for this account
        // was revoked" is part of the same story as the attempts that preceded it, and reading
        // the two in one timeline is the point.
        await audit
            .RecordAsync(
                SignInOutcome.SignedOutEverywhere, user.Username, user.Id, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(true);
    }
}
