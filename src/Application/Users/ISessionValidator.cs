namespace AiFramework.Application.Users;

/// <summary>
/// Answers the one question the cookie handler asks on every authenticated request: is the stamp
/// this session was issued with still the stored one. A mismatch means the session's authority
/// changed since it was minted - a password change, a lockout, a sign-out-everywhere - and the
/// session must be rejected. See ADR 0011.
/// </summary>
public interface ISessionValidator
{
    /// <summary>
    /// True only when <paramref name="userId"/> exists and its stored stamp equals
    /// <paramref name="stamp"/>. A missing user is false rather than an error: a session
    /// outliving its account is stale, not faulty.
    /// </summary>
    public Task<bool> IsStampCurrentAsync(
        Guid userId, string stamp, CancellationToken cancellationToken);
}
