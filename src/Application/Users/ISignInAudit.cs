namespace AiFramework.Application.Users;

/// <summary>
/// What happened on one attempt to authenticate.
/// </summary>
/// <remarks>
/// The distinction the CLIENT never sees. <c>SignInHandler</c> answers every failure with one
/// uniform error, deliberately — a differing message or timing between "no such user" and "wrong
/// password" is an account-enumeration oracle (ADR 0006). The audit records the difference
/// because an operator investigating an attack needs it; the response does not, because an
/// attacker must not have it.
/// </remarks>
public enum SignInOutcome
{
    Succeeded,

    /// <summary>The account exists and the password was wrong.</summary>
    BadCredentials,

    /// <summary>The account exists and was locked when the attempt arrived (ADR 0008).</summary>
    LockedOut,

    /// <summary>No account of that name. Recorded with the attempted username, not a user id.</summary>
    UnknownUser,

    /// <summary>The user revoked every session for their own account.</summary>
    SignedOutEverywhere,
}

/// <summary>
/// Records sign-in attempts for the monitoring page. See ADR 0021.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes immediately, outside any unit of work.</b> Three of the five outcomes are failures,
/// and <c>Behaviors.CommitAsync</c> commits only a successful <c>Result</c> — a tracked write
/// would be discarded without an error on exactly the attempts most worth recording. The same
/// constraint that already forces <c>TryRecordFailedSignInAsync</c> to bypass the unit of work.
/// </para>
/// <para>
/// A failure here must never fail the sign-in, and must never change what the caller is told.
/// </para>
/// </remarks>
public interface ISignInAudit
{
    /// <summary>
    /// Records one attempt. <paramref name="userId"/> is null for
    /// <see cref="SignInOutcome.UnknownUser"/>, which is the whole reason the attempted username
    /// is stored alongside it.
    /// </summary>
    public Task RecordAsync(
        SignInOutcome outcome,
        string usernameAttempted,
        Guid? userId,
        CancellationToken cancellationToken);
}
