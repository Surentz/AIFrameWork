namespace AiFramework.Infrastructure.Outbox;

public enum OutboxStatus
{
    Pending,
    InFlight,
    Processed,
    Dead,
}

/// <summary>One domain event, persisted in the same transaction as the aggregate that raised it.</summary>
public sealed class OutboxMessage
{
    public required Guid Id { get; init; }

    /// <summary>The stable registered name, e.g. "order.placed" — not the CLR type name.</summary>
    public required string EventName { get; init; }

    public required string Payload { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public OutboxStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public DateTimeOffset? LeasedUntil { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? LastError { get; set; }
}
