using AiFramework.Domain.Users;

namespace AiFramework.Application.Users;

/// <summary>
/// What the caller is currently allowed to be. Returned by a successful validation; the role is
/// read from the database on the same request that checks the stamp, and is never carried in the
/// cookie. See ADR 0020.
/// </summary>
public sealed record SessionAuthority(UserRole Role);

/// <summary>
/// Answers whether a presented session is still current, and with what authority. The stamp side
/// exists because a cookie outlives the state it was minted from: the user's stamp may have
/// changed since — a password change, a lockout, a sign-out-everywhere — and the session must be
/// rejected. See ADR 0011.
/// </summary>
public interface ISessionValidator
{
    /// <summary>
    /// The caller's authority, or <c>null</c> when the session must be rejected — either because
    /// <paramref name="userId"/> does not exist or because its stored stamp no longer equals
    /// <paramref name="stamp"/>. A missing user is null rather than an error: a session outliving
    /// its account is stale, not faulty.
    /// </summary>
    /// <remarks>
    /// Returns the role alongside the verdict rather than offering a second method for it. One
    /// call, one row, one round trip: the role read is free precisely because it rides on the
    /// stamp read that ADR 0011 already pays for on every authenticated request. Splitting them
    /// would double that cost for no benefit.
    /// </remarks>
    public Task<SessionAuthority?> ValidateAsync(
        Guid userId, string stamp, CancellationToken cancellationToken);
}
