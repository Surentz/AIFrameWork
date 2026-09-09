namespace AiFramework.Application.Abstractions;

/// <summary>Time, as a dependency. Never call DateTimeOffset.UtcNow directly.</summary>
public interface IClock
{
    public DateTimeOffset UtcNow { get; }
}

/// <summary>
/// The signed-in caller, or null when there is no session. Nullable rather than throwing:
/// the outbox pumps resolve scopes with no HTTP context at all, and a handler that returns
/// ErrorKind.Unauthorized produces a 401 where a throw would produce a 500.
/// </summary>
/// <remarks>
/// Once <see cref="Id"/> resolves to a non-null value within a scope, it must keep returning
/// that same value for the rest of the scope — a cacheable query's handler may run later, in
/// that same scope, from a context where re-deriving the caller from scratch is not possible.
/// An implementation that re-reads its source on every access breaks that for any consumer
/// reached through the caching behavior.
/// </remarks>
public interface ICurrentUser
{
    public Guid? Id { get; }
}

/// <summary>
/// Commits the current transaction. Handlers never call this — the unit-of-work behavior
/// does, exactly once, after a successful command.
/// </summary>
public interface IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
