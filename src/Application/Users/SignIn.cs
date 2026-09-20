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
            await RecordFailureAsync(user, now, cancellationToken).ConfigureAwait(false);

            return Result.Failure<SessionView>(Failed);
        }

        // Only when there is something to clear: an ordinary sign-in is the common case and must
        // not cost an UPDATE that writes the values already there.
        if (user.FailedSignInAttempts > 0 || user.LockedOutUntil is not null)
        {
            user.RegisterSuccessfulSignIn();
            await users.ClearSignInFailuresAsync(user.Id, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(
            new SessionView(user.Id, user.Username, user.DisplayName, user.SecurityStamp, user.Role));
    }

    /// <summary>
    /// Records the failure, retrying once if a concurrent attempt on the same account got there
    /// first. One retry, not a loop: the point is that a burst of concurrent guesses each advance
    /// the counter rather than all writing the same value, and a second lost race means the
    /// counter is moving anyway. Giving up silently is safe — the caller still gets the uniform
    /// failure, and this must never surface a different answer to the client.
    /// </summary>
    /// <remarks>
    /// This "lost race" retry cannot distinguish a genuine concurrent writer from
    /// AddInfrastructure's own EnableRetryOnFailure retrying the FIRST write transparently: if
    /// that write committed but its acknowledgment was lost to a transient fault, the retried
    /// <c>UPDATE ... WHERE FailedSignInAttempts = expected</c> matches zero rows for the same
    /// reason a real concurrent writer would — the counter has already moved — and
    /// TryRecordFailedSignInAsync reports it as a lost race either way. The consequence: this
    /// method's OWN retry then re-reads the already-advanced row and advances it again, so one
    /// failed sign-in can increment the counter twice. That is the safe direction for a lockout —
    /// it locks an account slightly earlier, never later — so it is accepted rather than
    /// distinguished. ADR 0014.
    /// </remarks>
    private async Task RecordFailureAsync(User user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expected = user.FailedSignInAttempts;
        var stampBefore = user.SecurityStamp;
        user.RegisterFailedSignIn(now);

        if (await users.TryRecordFailedSignInAsync(
                user.Id, expected, user.FailedSignInAttempts, user.LockedOutUntil,
                RotatedStampOrNull(user, stampBefore), cancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }

        var latest = await users
            .GetByNormalizedUsernameAsync(user.UsernameNormalized, cancellationToken)
            .ConfigureAwait(false);

        if (latest is null)
        {
            return;
        }

        var latestExpected = latest.FailedSignInAttempts;
        var latestStampBefore = latest.SecurityStamp;
        latest.RegisterFailedSignIn(now);

        _ = await users.TryRecordFailedSignInAsync(
                latest.Id, latestExpected, latest.FailedSignInAttempts, latest.LockedOutUntil,
                RotatedStampOrNull(latest, latestStampBefore), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The stamp to persist, or null to leave the stored one alone. Compares the stamp before and
    /// after <see cref="User.RegisterFailedSignIn"/> rather than asking whether the account is
    /// locked: on the race-lost retry the re-read row is usually locked already, in which case
    /// nothing rotated and writing the as-read stamp back could overwrite a rotation - a password
    /// change, a sign-out-everywhere - that landed between the re-read and this write. Non-null only
    /// when THIS call rotated, which by the domain rule is only the failure that locks. Rotating on
    /// every wrong guess would let anyone who knows a username sign that user out at will.
    /// </summary>
    private static string? RotatedStampOrNull(User user, string stampBefore) =>
        string.Equals(user.SecurityStamp, stampBefore, StringComparison.Ordinal) ? null : user.SecurityStamp;
}
