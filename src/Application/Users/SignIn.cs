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

public sealed class SignInHandler(IUserRepository users, IPasswordHasher hasher, IClock clock)
    : ICommandHandler<SignIn, SessionView>
{
    /// <summary>
    /// One error for every failure mode — unknown username, wrong password, locked out. A
    /// distinct "too many attempts" would turn this endpoint into a way to enumerate accounts,
    /// which is the same reason the unknown-username branch hashes and discards below.
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

        var now = clock.UtcNow;

        if (user.IsLockedOut(now))
        {
            // Hash and discard for the same timing reason as above: returning early here without
            // hashing would make a locked account answer measurably faster than a wrong password.
            // And deliberately no counter update — the window is fixed, so attempts made during
            // a lockout must not extend it.
            _ = hasher.Hash(command.Password);

            return Result.Failure<SessionView>(Failed);
        }

        if (!hasher.Verify(user.PasswordHash, command.Password))
        {
            user.RegisterFailedSignIn(now);
            await users
                .RecordSignInOutcomeAsync(
                    user.Id, user.FailedSignInAttempts, user.LockedOutUntil, cancellationToken)
                .ConfigureAwait(false);

            return Result.Failure<SessionView>(Failed);
        }

        // Only when there is something to clear: an ordinary sign-in is the common case and must
        // not cost an UPDATE that writes the values already there.
        if (user.FailedSignInAttempts > 0 || user.LockedOutUntil is not null)
        {
            user.RegisterSuccessfulSignIn();
            await users
                .RecordSignInOutcomeAsync(
                    user.Id, user.FailedSignInAttempts, user.LockedOutUntil, cancellationToken)
                .ConfigureAwait(false);
        }

        return Result.Success(new SessionView(user.Id, user.Username, user.DisplayName));
    }
}
