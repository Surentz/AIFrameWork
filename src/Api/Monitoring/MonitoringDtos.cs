using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
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

/// <summary>One attempt to authenticate.</summary>
public sealed record SignInEventResponse
{
    public required Guid Id { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>Null when the attempt named an account that does not exist.</summary>
    public Guid? UserId { get; init; }

    /// <summary>What was typed. The only record of an attempt against a name that does not exist.</summary>
    public required string UsernameAttempted { get; init; }

    /// <summary>
    /// The distinction the sign-in endpoint itself never reveals: it answers every failure
    /// identically, because a differing answer is an account-enumeration oracle (ADR 0006). An
    /// operator investigating an attack needs the difference; an attacker must not have it.
    /// </summary>
    public required SignInOutcome Outcome { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public string? TraceId { get; init; }
}

public sealed record SignInEventPageResponse
{
    public required IReadOnlyList<SignInEventResponse> Items { get; init; }

    public required int TotalCount { get; init; }

    public required int Page { get; init; }
}

public sealed record LockedOutUserResponse
{
    public required Guid UserId { get; init; }

    public required string Username { get; init; }

    public required DateTimeOffset LockedOutUntil { get; init; }
}

/// <summary>Sign-in counts, who is locked out, and who is about.</summary>
public sealed record SignInHealthResponse
{
    public required int Succeeded { get; init; }

    public required int BadCredentials { get; init; }

    public required int LockedOut { get; init; }

    public required int UnknownUser { get; init; }

    public required DateTimeOffset Since { get; init; }

    /// <summary>
    /// Users seen within the active window. A cookie session has no logout event to key off, so
    /// "online" can only ever mean "seen recently" — the window says how recently.
    /// </summary>
    public required int ActiveUsers { get; init; }

    public required int ActiveWindowMinutes { get; init; }

    public required IReadOnlyList<LockedOutUserResponse> LockedOutUsers { get; init; }
}
