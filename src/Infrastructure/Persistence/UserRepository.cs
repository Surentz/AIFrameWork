using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using AiFramework.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiFramework.Infrastructure.Persistence;

public sealed class UserRepository(AiFrameworkDbContext context) : IUserRepository
{
    public async Task<bool> TryAddAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        await context.Users.AddAsync(user, cancellationToken).ConfigureAwait(false);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        // Only the username index, by name. Any other failure - another constraint, a lost
        // connection - is not "that name is taken" and must stay a 500 rather than be reported as
        // one. Not a transient fault either, so the retrying execution strategy rethrows it as
        // the DbUpdateException itself rather than wrapping it.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: UserConfiguration.UsernameIndex,
        })
        {
            // Left Added, the entity would be written again by the unit of work's commit - and
            // fail the same way - were anything else in the request to succeed afterwards.
            context.Entry(user).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// Tracked, unlike the reads in <see cref="OrderRepository"/>: ChangePassword mutates the
    /// user it loads here, and a no-tracking read would leave the new hash unsaved with no error.
    /// </summary>
    public Task<User?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        context.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <summary>No-tracking: sign-in and the availability check both only read.</summary>
    public Task<User?> GetByNormalizedUsernameAsync(
        string usernameNormalized, CancellationToken cancellationToken) =>
        context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UsernameNormalized == usernameNormalized, cancellationToken);

    /// <summary>
    /// ExecuteUpdateAsync, not a tracked mutation: it issues its own UPDATE straight away, so it
    /// does not depend on the unit of work committing. The FailedSignInAttempts clause in the
    /// Where is what makes the write conditional — the row still has to hold the value the caller
    /// read before its PBKDF2 verify, or another concurrent attempt has already moved it and this
    /// write must not flatten theirs. See the port's documentation.
    /// </summary>
    public async Task<bool> TryRecordFailedSignInAsync(
        Guid userId,
        int expectedAttempts,
        int attempts,
        DateTimeOffset? lockedOutUntil,
        string? rotatedSecurityStamp,
        CancellationToken cancellationToken)
    {
        var updated = await context.Users
            .Where(u => u.Id == userId && u.FailedSignInAttempts == expectedAttempts)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.FailedSignInAttempts, attempts)
                    .SetProperty(u => u.LockedOutUntil, lockedOutUntil)
                    // Coalesce rather than a second query shape: null means "keep what is stored",
                    // so one statement serves both the ordinary failure and the locking one.
                    .SetProperty(u => u.SecurityStamp, u => rotatedSecurityStamp ?? u.SecurityStamp),
                cancellationToken)
            .ConfigureAwait(false);

        return updated > 0;
    }

    /// <summary>
    /// Tracked, like <see cref="GetAsync"/> and unlike the other reads here: the reconcile
    /// handler mutates what this returns, and a no-tracking read would leave every role change
    /// unsaved with no error at all.
    /// </summary>
    public async Task<IReadOnlyList<User>> ListForRoleReconciliationAsync(
        string[] usernamesNormalized, CancellationToken cancellationToken) =>
        await context.Users
            .Where(u => usernamesNormalized.Contains(u.UsernameNormalized))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// An arbitrary but fixed key identifying the demotion lock. Any constant works; it only has
    /// to be the same one for every caller.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than private so <c>UserRepositoryDemotionTests</c> can take the very
    /// same lock from another connection to prove this method waits on it. A test that hard-coded
    /// the number could drift from the implementation and then prove nothing.
    /// </remarks>
    internal const long DemotionLockKey = 0x41_44_4D_4E; // "ADMN"

    /// <summary>
    /// The last-administrator rail: demote, unless this is the only administrator left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The conditional UPDATE alone is NOT enough, and that was established by experiment
    /// rather than reasoning.</b> A first version of this was one statement whose WHERE clause
    /// carried an <c>EXISTS</c> over the other administrators. That closes the read-then-write
    /// gap, but a subquery read takes no locks: two overlapping transactions demoting DIFFERENT
    /// administrators each fail to see the other's uncommitted change, both pass the EXISTS, and
    /// the table ends with nobody. <c>UserRepositoryDemotionTests</c> demonstrates exactly that,
    /// and fails against the one-statement version.
    /// </para>
    /// <para>
    /// A transaction-scoped advisory lock serializes every demotion against every other one, so
    /// the EXISTS is evaluated while no competing demotion can be in flight. It is a global lock
    /// on an operation a system performs a handful of times a year, so the contention it creates
    /// is not a cost worth optimising away — and the alternatives (<c>FOR UPDATE</c> inside the
    /// subquery, or SERIALIZABLE) trade it for deadlocks or serialization failures that the
    /// caller would then have to handle.
    /// </para>
    /// <para>
    /// The explicit transaction goes through the execution strategy, per ADR 0014 — a bare
    /// <c>BeginTransactionAsync</c> throws once <c>EnableRetryOnFailure</c> is configured,
    /// because the strategy cannot retry a block it does not own.
    /// </para>
    /// </remarks>
    public Task<bool> TryDemoteAsync(Guid userId, CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Released when the transaction ends, however it ends - which is what makes this
            // safe against a connection returned to the pool mid-failure, unlike a session-scoped
            // pg_advisory_lock that someone has to remember to release.
            await context.Database
                .ExecuteSqlAsync(
                    $"SELECT pg_advisory_xact_lock({DemotionLockKey})", cancellationToken)
                .ConfigureAwait(false);

            var demoted = await context.Users
                .Where(u => u.Id == userId
                    && u.Role == UserRole.Admin
                    && context.Users.Any(other => other.Role == UserRole.Admin && other.Id != userId))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(u => u.Role, UserRole.Member),
                    cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return demoted > 0;
        });
    }

    /// <summary>
    /// No-tracking and projected: this only ever displays, and materialising entities here would
    /// put every listed account into the change tracker on a screen whose next action mutates one
    /// of them.
    /// </summary>
    /// <remarks>
    /// Ordered by LastSeenAt descending with nulls last, so the accounts an operator is most
    /// likely looking for are on the first page, and an account that has never signed in does not
    /// sort above everyone.
    /// </remarks>
    public async Task<(IReadOnlyList<AdministeredUserRow> Rows, int Total)> ListAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = context.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // ILIKE through EF.Functions rather than ToLower().Contains(): the latter builds a
            // lower(...) call around the COLUMN, which no index can serve.
            var pattern = $"%{search.Trim()}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.Username, pattern)
                || EF.Functions.ILike(u.DisplayName, pattern));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await query
            .OrderByDescending(u => u.LastSeenAt.HasValue)
            .ThenByDescending(u => u.LastSeenAt)
            .ThenBy(u => u.UsernameNormalized)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new AdministeredUserRow(
                u.Id, u.Username, u.DisplayName, u.Role, u.RegisteredAt, u.LastSeenAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (rows, total);
    }

    /// <summary>
    /// The throttle is the WHERE clause. No read, so nothing to race: a request whose row was
    /// already stamped inside the window matches zero rows and writes nothing at all.
    /// </summary>
    public Task TouchLastSeenAsync(
        Guid userId,
        DateTimeOffset now,
        DateTimeOffset staleBefore,
        CancellationToken cancellationToken) =>
        context.Users
            .Where(u => u.Id == userId && (u.LastSeenAt == null || u.LastSeenAt < staleBefore))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(u => u.LastSeenAt, now),
                cancellationToken);

    /// <summary>
    /// Unconditional, unlike the failure write above: clearing is idempotent and a successful
    /// sign-in should win over any concurrent failed one. Immediate for the same reason.
    /// </summary>
    public Task ClearSignInFailuresAsync(Guid userId, CancellationToken cancellationToken) =>
        context.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.FailedSignInAttempts, 0)
                    .SetProperty(u => u.LockedOutUntil, (DateTimeOffset?)null),
                cancellationToken);
}
