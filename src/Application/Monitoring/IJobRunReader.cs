using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>
/// Reads what <see cref="IJobRunRecorder"/> wrote. A separate port from the recorder because the
/// two have opposite lifetimes and opposite hosts: the worker writes and never reads, the API
/// reads and never writes.
/// </summary>
public interface IJobRunReader
{
    public Task<JobRunPage> ListAsync(
        JobRunStatus? status,
        string? jobName,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    public Task<JobRunView?> GetAsync(Guid envelopeId, int attempt, CancellationToken cancellationToken);

    /// <summary>Counts by status for runs started at or after <paramref name="since"/>.</summary>
    public Task<IReadOnlyDictionary<JobRunStatus, int>> CountByStatusAsync(
        DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>
/// Wolverine's dead-letter store, behind a port so <c>Application</c> never names Wolverine.
/// </summary>
/// <remarks>
/// The adapter uses Wolverine's own <c>IDeadLetters</c> API rather than raw SQL against its
/// tables. That is a deliberate change from this feature's plan, which assumed there was no
/// supported surface: there is, it covers both reading and replaying, and using it means this
/// application is not coupled to a schema Wolverine owns and migrates.
/// </remarks>
public interface IDeadLetterStore
{
    public Task<DeadLetterPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Marks one dead-lettered message replayable. The worker picks it up from the shared
    /// PostgreSQL store on its own — the API never talks to the worker. Returns false when no such
    /// message is in the dead-letter queue.
    /// </summary>
    public Task<bool> ReplayAsync(Guid messageId, CancellationToken cancellationToken);
}
