using AiFramework.Domain.Users;

namespace AiFramework.Application.Users;

public interface IUserRepository
{
    public Task AddAsync(User user, CancellationToken cancellationToken);

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
    /// The users a role reconcile could possibly change: everyone currently holding
    /// <see cref="UserRole.Admin"/>, plus everyone named in the configured administrator list.
    /// Tracked, because the caller mutates what it reads.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT every user. The result set is bounded by the size of the configured list
    /// plus the number of current administrators — both small by nature — so this stays a bounded
    /// read however many accounts exist. Anyone outside both sets is already a Member and already
    /// unlisted, so loading them could only ever produce a no-op.
    /// </remarks>
    /// <param name="usernamesNormalized">
    /// Already through <see cref="User.Normalize"/>. Raw usernames match nothing.
    /// </param>
    /// <param name="cancellationToken">Propagated to the query.</param>
    public Task<IReadOnlyList<User>> ListForRoleReconciliationAsync(
        string[] usernamesNormalized, CancellationToken cancellationToken);
}
