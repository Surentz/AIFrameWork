namespace AiFramework.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int BatchSize { get; init; } = 20;

    public int ChannelCapacity { get; init; } = 100;

    public int WorkerCount { get; init; } = 2;

    public int MaxAttempts { get; init; } = 5;

    /// <summary>Must comfortably exceed the slowest handler, or a healthy message is reclaimed and run twice.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How far back PruneAsync keeps a Processed row. Read by the scheduled
    /// <c>PruneProcessedOutbox</c> job through <c>OutboxRetention</c> (ADR 0017), not by this
    /// class directly — the poll loop no longer sweeps for itself.
    /// </summary>
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(7);
}
