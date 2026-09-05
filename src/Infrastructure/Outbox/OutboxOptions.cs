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

    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How often the retention sweep runs. Deliberately NOT every poll cycle: PruneAsync deletes
    /// Processed rows older than RetentionPeriod, which is measured in days, so running it at
    /// PollInterval issued roughly 86,000 no-op DELETEs a day against an idle system — and, because
    /// PruneAsync goes through EF (ClaimAsync uses a raw NpgsqlCommand and is not logged), every one
    /// of them was logged at Information, which is what made the console unreadable in development.
    /// </summary>
    public TimeSpan PruneInterval { get; init; } = TimeSpan.FromMinutes(5);
}
