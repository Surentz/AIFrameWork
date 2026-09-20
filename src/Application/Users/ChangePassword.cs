using AiFramework.Application.Abstractions;
using AiFramework.Domain.Users;
using FluentValidation;

namespace AiFramework.Application.Users;

/// <summary>
/// Returns the caller's session view rather than a bool: <see cref="User.ChangePassword"/>
/// rotates the security stamp, so the cookie that made this request is stale the moment it
/// succeeds. AuthController re-issues it from the value returned here, which is what keeps the
/// person changing their own password signed in while every other session for them ends.
/// See ADR 0011.
/// </summary>
public sealed record ChangePassword(Guid UserId, string CurrentPassword, string NewPassword)
    : ICommand<SessionView>;

public sealed class ChangePasswordValidator : AbstractValidator<ChangePassword>
{
    public ChangePasswordValidator()
    {
        RuleFor(c => c.CurrentPassword).NotEmpty();
        RuleFor(c => c.NewPassword).NotEmpty().MinimumLength(PasswordPolicy.MinimumLength);
    }
}

public sealed class ChangePasswordHandler(IUserRepository users, IPasswordHasher hasher)
    : ICommandHandler<ChangePassword, SessionView>
{
    public async Task<Result<SessionView>> HandleAsync(
        ChangePassword command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var user = await users.GetAsync(command.UserId, cancellationToken).ConfigureAwait(false);

        // Unauthorized rather than NotFound: the id comes from the caller's own session, so a
        // missing row means that session is stale, not that they asked for someone else's user.
        if (user is null)
        {
            return Result.Failure<SessionView>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        if (!hasher.Verify(user.PasswordHash, command.CurrentPassword))
        {
            return Result.Failure<SessionView>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "Your current password is not correct."));
        }

        user.ChangePassword(hasher.Hash(command.NewPassword));

        // Carries the ROTATED stamp - ChangePassword rotated it a line ago.
        return Result.Success(
            new SessionView(user.Id, user.Username, user.DisplayName, user.SecurityStamp, user.Role));
    }
}
