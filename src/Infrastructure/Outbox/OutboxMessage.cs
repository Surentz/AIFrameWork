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

    /// <summary>
    /// The W3C traceparent of the request that raised this event (Activity.Current?.Id, captured
    /// by DomainEventsInterceptor at the same point it copies the event off the aggregate), or
    /// null when there was no ambient Activity. Restored by OutboxWorkItemProcessor as the parent
    /// of the delivery Activity, so a delivery's own logs — and anything a handler logs — carry
    /// the SAME TraceId as the request that caused them, not a fresh unrelated one. Set once, at
    /// creation, never updated afterward — unlike Status and the columns below it.
    /// </summary>
    public string? TraceParent { get; init; }

    public OutboxStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public DateTimeOffset? LeasedUntil { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? LastError { get; set; }
}
