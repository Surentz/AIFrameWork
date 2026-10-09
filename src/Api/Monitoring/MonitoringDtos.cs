using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
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

    /// <summary>
    /// The log store's URL for one trace, with <c>{traceId}</c> where the 32-hex id goes; null
    /// when none is configured, in which case trace ids are shown as text. Carried here because
    /// every monitoring page already asks this endpoint, and because it is admin-only: a member
    /// never learns where the log store is.
    /// </summary>
    public string? TraceLinkTemplate { get; init; }
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

/// <summary>One endpoint's or handler's traffic over a window.</summary>
public sealed record TrafficRowResponse
{
    public required TrafficKind Kind { get; init; }

    /// <summary>A route template for HTTP, a request type's name otherwise. Never a raw path.</summary>
    public required string Name { get; init; }

    public required long Total { get; init; }

    public required long Failed { get; init; }

    public required long Faulted { get; init; }

    public double? MeanMs { get; init; }

    /// <summary>
    /// Interpolated from summed histogram buckets, not derived from the mean — a mean cannot give
    /// a percentile, and averaging per-pod means would be wrong even if it could. Null when the
    /// window holds no measurements.
    /// </summary>
    public double? P50Ms { get; init; }

    public double? P95Ms { get; init; }

    public double? P99Ms { get; init; }
}

public sealed record TrafficSummaryResponse
{
    public required DateTimeOffset Since { get; init; }

    public required double RequestsPerMinute { get; init; }

    /// <summary>Failed plus faulted, over total. Zero when nothing was measured.</summary>
    public required double ErrorRate { get; init; }

    public required TrafficRowResponse Overall { get; init; }

    public required IReadOnlyList<TrafficRowResponse> Rows { get; init; }
}

/// <summary>One minute of the whole application's traffic.</summary>
public sealed record TrafficPointResponse
{
    public required DateTimeOffset BucketStart { get; init; }

    public required long Total { get; init; }

    public required long Failed { get; init; }

    public required long Faulted { get; init; }

    public double? P95Ms { get; init; }
}

public sealed record TrafficSeriesResponse
{
    public required DateTimeOffset Since { get; init; }

    public required IReadOnlyList<TrafficPointResponse> Points { get; init; }
}

/// <summary>One account on the user-management screen.</summary>
public sealed record AdministeredUserResponse
{
    public required Guid Id { get; init; }

    public required string Username { get; init; }

    public required string DisplayName { get; init; }

    public required UserRole Role { get; init; }

    /// <summary>
    /// Whether this username is named in <c>Admin__Usernames</c>. Configuration is a floor
    /// (ADR 0022): demoting an account that is still listed there is undone at the next API
    /// start, so the screen warns before the click rather than after the restart.
    /// </summary>
    public required bool RoleIsConfigured { get; init; }

    public required DateTimeOffset RegisteredAt { get; init; }

    /// <summary>Null for an account that has never made an authenticated request.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }
}

public sealed record AdministeredUserPageResponse
{
    public required IReadOnlyList<AdministeredUserResponse> Items { get; init; }

    public required int TotalCount { get; init; }

    public required int Page { get; init; }
}

/// <summary>What an administrator did to an account.</summary>
public sealed record AdminActionResponse
{
    public required Guid Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required AdminActionKind Kind { get; init; }

    public required Guid ActorUserId { get; init; }

    public required string ActorUsername { get; init; }

    public required Guid TargetUserId { get; init; }

    public required string TargetUsername { get; init; }

    public string? IpAddress { get; init; }

    public string? TraceId { get; init; }
}

public sealed record AdminActionPageResponse
{
    public required IReadOnlyList<AdminActionResponse> Items { get; init; }

    public required int TotalCount { get; init; }

    public required int Page { get; init; }
}

/// <summary>The role to move an account to.</summary>
public sealed record ChangeUserRoleRequest
{
    public required UserRole Role { get; init; }
}

/// <summary>Every external system's last health and last hour of traffic.</summary>
public sealed record ExternalSystemsResponse
{
    public required DateTimeOffset TrafficSince { get; init; }

    public required IReadOnlyList<ExternalSystemRowResponse> Systems { get; init; }
}

/// <summary>One external system. <see cref="State"/> is null until the worker has checked it once.</summary>
public sealed record ExternalSystemRowResponse
{
    public required string Name { get; init; }

    public ExternalSystemState? State { get; init; }

    /// <summary>The worst check's description. Never a host, path, body or secret.</summary>
    public string? Description { get; init; }

    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>The worker has not rewritten this row for three minutes: it is history, not health.</summary>
    public required bool Stale { get; init; }

    public DateTimeOffset? CertificateNotAfter { get; init; }

    /// <summary>Null when the system uses no token.</summary>
    public bool? TokenOk { get; init; }

    public required long Calls { get; init; }

    public required long Failed { get; init; }

    public required long Faulted { get; init; }

    public required long Attempts { get; init; }

    public double? P95Ms { get; init; }
}
