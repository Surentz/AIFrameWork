using AiFramework.Application.Abstractions;
using AiFramework.Domain.Users;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// Who is looking at the monitoring page.
/// </summary>
public sealed record MonitoringAccessResponse
{
    public required Guid UserId { get; init; }

    public required string Username { get; init; }

    /// <summary>
    /// Always <see cref="UserRole.Admin"/> on a successful response — the policy admits nobody
    /// else. Returned anyway so the page can render who it is showing, and so a future read-only
    /// operator role has somewhere to appear without a contract change.
    /// </summary>
    public required UserRole Role { get; init; }
}

/// <summary>One attempt at one job.</summary>
public sealed record JobRunResponse
{
    /// <summary>Wolverine's message id. Stable across every attempt at the same job.</summary>
    public required Guid EnvelopeId { get; init; }

    public required int Attempt { get; init; }

    public required string JobName { get; init; }

    /// <summary>Null only for a job missing from the registration list.</summary>
    public JobLane? Lane { get; init; }

    public required JobRunStatus Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public long? DurationMs { get; init; }

    public Guid? OwnerId { get; init; }

    /// <summary>The exception's type and message. The full stack is in the log store.</summary>
    public string? Error { get; init; }

    /// <summary>The W3C trace id, for the deep link into the log store.</summary>
    public string? TraceId { get; init; }

    public string? InstanceId { get; init; }
}

public sealed record JobRunPageResponse
{
    public required IReadOnlyList<JobRunResponse> Items { get; init; }

    public required int TotalCount { get; init; }

    public required int Page { get; init; }
}

/// <summary>A message that exhausted its retries, from Wolverine's own dead-letter queue.</summary>
public sealed record DeadLetterResponse
{
    public required Guid Id { get; init; }

    public required string MessageType { get; init; }

    public required string ExceptionType { get; init; }

    public required string ExceptionMessage { get; init; }

    public required DateTimeOffset SentAt { get; init; }

    public string? ReceivedAt { get; init; }

    /// <summary>True once an operator has asked for it to be retried.</summary>
    public required bool Replayable { get; init; }
}

public sealed record DeadLetterPageResponse
{
    public required IReadOnlyList<DeadLetterResponse> Items { get; init; }

    public required int TotalCount { get; init; }

    public required int Page { get; init; }
}

/// <summary>Counts by outcome over a trailing window, for the overview tiles.</summary>
public sealed record JobHealthResponse
{
    public required int Running { get; init; }

    public required int Succeeded { get; init; }

    public required int Failed { get; init; }

    /// <summary>
    /// The WHOLE dead-letter queue, not just the window: a message stuck for a week is exactly
    /// the one worth seeing, and windowing it would hide the oldest failures behind the newest.
    /// </summary>
    public required int DeadLettered { get; init; }

    public required DateTimeOffset Since { get; init; }

    /// <summary>The scheduled jobs this operator may start on demand.</summary>
    public required IReadOnlyList<string> TriggerableJobs { get; init; }
}

public sealed record TriggerJobRequest
{
    public required string JobName { get; init; }
}
