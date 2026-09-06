using AiFramework.Application.Abstractions;
using FluentValidation;

namespace AiFramework.Application.Users;

/// <summary>
/// <c>bool</c> because <see cref="Result{T}"/> has no unit type and there is no non-generic
/// <c>Result</c>; the value is always true and carries no information.
/// </summary>
public sealed record ChangePassword(Guid UserId, string CurrentPassword, string NewPassword)
    : ICommand<bool>;

public sealed class ChangePasswordValidator : AbstractValidator<ChangePassword>
{
    public ChangePasswordValidator()
    {
        RuleFor(c => c.CurrentPassword).NotEmpty();
        RuleFor(c => c.NewPassword).NotEmpty().MinimumLength(PasswordPolicy.MinimumLength);
    }
}

public sealed class ChangePasswordHandler(IUserRepository users, IPasswordHasher hasher)
    : ICommandHandler<ChangePassword, bool>
{
    public async Task<Result<bool>> HandleAsync(
        ChangePassword command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var user = await users.GetAsync(command.UserId, cancellationToken).ConfigureAwait(false);

        // Unauthorized rather than NotFound: the id comes from the caller's own session, so a
        // missing row means that session is stale, not that they asked for someone else's user.
        if (user is null)
        {
            return Result.Failure<bool>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        if (!hasher.Verify(user.PasswordHash, command.CurrentPassword))
        {
            return Result.Failure<bool>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "Your current password is not correct."));
        }

        user.ChangePassword(hasher.Hash(command.NewPassword));

        return Result.Success(true);
    }
}
