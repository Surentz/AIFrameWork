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
