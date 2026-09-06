using AiFramework.Application.Abstractions;
using AiFramework.Domain.Users;
using FluentValidation;

namespace AiFramework.Application.Users;

public sealed record SignIn(string Username, string Password) : ICommand<SessionView>;

/// <summary>
/// Presence only. Sign-in must not enforce the password policy: an account whose password
/// predates a policy change still has to be able to get in, and echoing the rules back on a
/// failed sign-in hands an attacker the policy for free.
/// </summary>
public sealed class SignInValidator : AbstractValidator<SignIn>
{
    public SignInValidator()
    {
        RuleFor(c => c.Username).NotEmpty();
        RuleFor(c => c.Password).NotEmpty();
    }
}

public sealed class SignInHandler(IUserRepository users, IPasswordHasher hasher)
    : ICommandHandler<SignIn, SessionView>
{
    /// <summary>
    /// One error for every failure mode. A distinct "no such user" would turn this endpoint into
    /// a way to enumerate accounts.
    /// </summary>
    private static readonly Error Failed = new(
        ErrorKind.Unauthorized, "auth.failed", "That username and password do not match.");

    public async Task<Result<SessionView>> HandleAsync(
        SignIn command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var user = await users
            .GetByNormalizedUsernameAsync(User.Normalize(command.Username), cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            // Hash the supplied password and throw the result away. Hashing costs the same order
            // as verifying, so an unknown username takes about as long as a known one with the
            // wrong password - without this, response time answers "does this account exist?".
            _ = hasher.Hash(command.Password);

            return Result.Failure<SessionView>(Failed);
        }

        return hasher.Verify(user.PasswordHash, command.Password)
            ? Result.Success(new SessionView(user.Id, user.Username, user.DisplayName))
            : Result.Failure<SessionView>(Failed);
    }
}
