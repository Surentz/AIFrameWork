namespace AiFramework.Application.Abstractions;

/// <summary>Time, as a dependency. Never call DateTimeOffset.UtcNow directly.</summary>
public interface IClock
{
    public DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Commits the current transaction. Handlers never call this — the unit-of-work behavior
/// does, exactly once, after a successful command.
/// </summary>
public interface IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
