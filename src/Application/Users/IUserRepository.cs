using AiFramework.Domain.Users;

namespace AiFramework.Application.Users;

public interface IUserRepository
{
    /// <summary>
    /// Inserts a new account, and returns false when its username is already taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writes immediately, not at the unit of work's commit. The database's unique index is the
    /// only real guard on a username: two simultaneous registrations of one name both pass
    /// <c>RegisterUserHandler</c>'s pre-check, and the loser's insert fails. Committed later by
    /// <c>Behaviors.CommitAsync</c>, that failure surfaced as an unhandled exception and a 500 —
    /// after the handler had already reported success. Writing here puts it back inside the
    /// handler, which answers the same 409 the pre-check does.
    /// </para>
    /// <para>
    /// Unlike the other immediate writes on this port, this one goes through
    /// <c>SaveChangesAsync</c>, so the domain-events interceptor still sees it.
    /// </para>
    /// </remarks>
    public Task<bool> TryAddAsync(User user, CancellationToken cancellationToken);

    public Task<User?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Keyed on the normalized form, so lookups must pass <see cref="User.Normalize"/>'s output
    /// rather than the raw username.
    /// </summary>
    public Task<User?> GetByNormalizedUsernameAsync(
        string usernameNormalized, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed sign-in, but only if the stored counter still holds
    /// <paramref name="expectedAttempts"/>. Returns false when another request updated the row first,
    /// so the caller can re-read and retry rather than silently overwriting the other's increment.
    /// <para>
    /// Conditional because the caller reads the counter, then spends a PBKDF2 verification deciding
    /// what to write. Overlapping sign-in attempts on one account are the normal case during an
    /// attack — an unconditional write would let N concurrent guesses advance the counter by one.
    /// </para>
    /// <para>
    /// Writes immediately, independently of the unit of work: <c>Behaviors.CommitAsync</c> commits
    /// only a successful Result, and this is the failure path. A tracked mutation there would be
    /// discarded without an error, leaving the counter at zero forever and the lockout dead.
    /// </para>
    /// <para>
    /// Writing outside the unit of work also means bypassing <c>SaveChangesAsync</c>, and with it
    /// <c>DomainEventsInterceptor</c>: a domain event raised on this path would never reach the
    /// outbox. <see cref="User"/> raises none today, so nothing is broken — but adding one to
    /// <see cref="User.RegisterFailedSignIn"/> would compile, pass its unit tests, and silently
    /// go nowhere.
    /// </para>
    /// </summary>
    /// <param name="userId">The account to update.</param>
    /// <param name="expectedAttempts">
    /// The counter value the caller read before verifying the password. The write is refused if the
    /// stored value has since moved.
    /// </param>
    /// <param name="attempts">The counter value to store.</param>
    /// <param name="lockedOutUntil">The lockout expiry to store, or null to clear it.</param>
    /// <param name="rotatedSecurityStamp">
    /// The new stamp to store, or null to leave the existing one. Non-null only on the failure that
    /// locks the account: the rotation is what cuts off a session that is already signed in, and it
    /// cannot ride on the tracked entity because this write bypasses the unit of work. See ADR 0011.
    /// </param>
    /// <param name="cancellationToken">Propagated to the underlying <c>ExecuteUpdateAsync</c>.</param>
    public Task<bool> TryRecordFailedSignInAsync(
        Guid userId,
        int expectedAttempts,
        int attempts,
        DateTimeOffset? lockedOutUntil,
        string? rotatedSecurityStamp,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears the failure counter and any lockout after a successful sign-in. Unconditional
    /// deliberately: clearing is idempotent, and a successful sign-in should win over any concurrent
    /// failed one. Writes immediately, for the same reason as above — and bypasses domain-event
    /// dispatch for the same reason too.
    /// </summary>
    public Task ClearSignInFailuresAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// The users a role reconcile could possibly change: everyone named in the configured
    /// administrator list, and nobody else. Tracked, because the caller mutates what it reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT every user, and — since ADR 0022 — no longer the current administrators
    /// either. The reconcile only ever promotes, so an administrator this list does not name is
    /// left exactly as they are and loading them could produce nothing but a no-op. The result
    /// set is therefore bounded by the configured list alone, which is small by nature, however
    /// many accounts exist.
    /// </para>
    /// <para>
    /// That narrowing is what makes in-app administration possible at all: while this returned
    /// the current administrators too, the handler demoted every one of them absent from the
    /// list, and a grant made anywhere else could not survive a restart.
    /// </para>
    /// </remarks>
    /// <param name="usernamesNormalized">
    /// Already through <see cref="User.Normalize"/>. Raw usernames match nothing.
    /// </param>
    /// <param name="cancellationToken">Propagated to the query.</param>
    public Task<IReadOnlyList<User>> ListForRoleReconciliationAsync(
        string[] usernamesNormalized, CancellationToken cancellationToken);

    /// <summary>
    /// Demotes an administrator to <see cref="UserRole.Member"/>, unless they are the last one.
    /// Returns false when the demotion was refused for that reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two mechanisms, and the second was added only after the first was shown to be
    /// insufficient.</b> The condition lives in the statement's own WHERE clause rather than in a
    /// read followed by a write, which closes the obvious gap — but that alone is NOT enough, and
    /// believing it was is the mistake this paragraph exists to prevent someone repeating:
    /// </para>
    /// <code>
    /// UPDATE users SET "Role" = 'Member'
    /// WHERE "Id" = @id AND EXISTS (SELECT 1 FROM users WHERE "Role" = 'Admin' AND "Id" &lt;&gt; @id)
    /// </code>
    /// <para>
    /// A subquery read takes no locks. Two OVERLAPPING transactions demoting DIFFERENT
    /// administrators each fail to see the other's uncommitted change under READ COMMITTED, both
    /// pass the EXISTS, and the table ends with nobody. That was demonstrated against real
    /// Postgres rather than reasoned about.
    /// </para>
    /// <para>
    /// So the implementation also takes a transaction-scoped advisory lock, which serialises
    /// every demotion against every other one and is what makes the EXISTS trustworthy. It is a
    /// global lock on an operation performed a handful of times a year, so the contention is not
    /// worth optimising away. <c>UserRepositoryDemotionTests</c> proves the method waits on that
    /// lock; it deliberately does NOT try to win a race, because two attempts to do so both
    /// passed against the broken version — EF and Npgsql serialise concurrent calls in practice,
    /// so the interleaving cannot be produced on demand.
    /// </para>
    /// <para>
    /// The cost is that this write does NOT participate in the caller's unit of work: it commits
    /// its own transaction immediately. The audit row still commits with the caller's, so a failed
    /// commit can leave a demotion recorded nowhere. That is the safer direction of the two — the
    /// role is read fresh on every request, so the effect is real and visible either way, whereas
    /// an audit row for a demotion that never happened would make the record lie.
    /// </para>
    /// </remarks>
    public Task<bool> TryDemoteAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// One page of accounts for the user-management screen, newest-seen first, with the total
    /// matching count for paging.
    /// </summary>
    /// <param name="search">
    /// Matched case-insensitively against the username and the display name. Null or blank
    /// returns everyone.
    /// </param>
    /// <param name="page">One-based.</param>
    /// <param name="pageSize">Already clamped by the caller.</param>
    /// <param name="cancellationToken">Propagated to the query.</param>
    public Task<(IReadOnlyList<AdministeredUserRow> Rows, int Total)> ListAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Stamps <see cref="User.LastSeenAt"/>, but only if the stored value is older than
    /// <paramref name="staleBefore"/>. Returns nothing: the caller cannot act on the outcome and
    /// must not wait to find out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One conditional UPDATE, never read-modify-write.</b> This runs on the session-validation
    /// path, which is every authenticated request — ADR 0011 already pays one uncached read there.
    /// The throttle lives in the statement's own WHERE clause so the cost is at most one write per
    /// user per minute regardless of request volume, and there is no read to race with.
    /// </para>
    /// <para>
    /// Writes immediately, outside the unit of work, for the same reason the failed-sign-in
    /// counter does: there is no command in flight to commit it.
    /// </para>
    /// </remarks>
    public Task TouchLastSeenAsync(
        Guid userId, DateTimeOffset now, DateTimeOffset staleBefore, CancellationToken cancellationToken);
}

/// <summary>
/// A projection of one account for the user-management screen — never the entity, so a read that
/// only ever displays cannot accidentally become a write. See ADR 0022.
/// </summary>
public sealed record AdministeredUserRow(
    Guid Id,
    string Username,
    string DisplayName,
    UserRole Role,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? LastSeenAt);
