using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Monitoring;

/// <summary>One attempt at one job, as the monitoring page shows it.</summary>
public sealed record JobRunView(
    Guid EnvelopeId,
    int Attempt,
    string JobName,
    JobLane? Lane,
    JobRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long? DurationMs,
    Guid? OwnerId,
    string? Error,
    string? TraceId,
    string? InstanceId);

/// <summary>
/// A page of runs. Page number rather than a cursor, unlike <c>GetOrders</c>: an operator jumps
/// around and sorts by several columns, and Wolverine's own dead-letter API is page-numbered too,
/// so one paging style serves the whole feature.
/// </summary>
public sealed record JobRunPage(IReadOnlyList<JobRunView> Items, int TotalCount, int Page);

/// <summary>
/// A message that exhausted its retries, straight from Wolverine's own store.
/// </summary>
/// <remarks>
/// Not a <c>job_runs</c> row: dead-lettering is Wolverine's decision, taken after the handler
/// returns and outside anything <c>JobRunMiddleware</c> can observe. The two are joined by
/// <see cref="Id"/>, which is the same envelope id <c>JobRunView.EnvelopeId</c> carries. ADR 0021.
/// </remarks>
public sealed record DeadLetterView(
    Guid Id,
    string MessageType,
    string ExceptionType,
    string ExceptionMessage,
    DateTimeOffset SentAt,
    string? ReceivedAt,
    bool Replayable);

public sealed record DeadLetterPage(IReadOnlyList<DeadLetterView> Items, int TotalCount, int Page);

/// <summary>Counts by outcome over a window, for the overview tiles.</summary>
public sealed record JobHealthView(
    int Running, int Succeeded, int Failed, int DeadLettered, DateTimeOffset Since);

/// <summary>One attempt to authenticate, as the monitoring page shows it.</summary>
public sealed record SignInEventView(
    Guid Id,
    DateTimeOffset At,
    Guid? UserId,
    string UsernameAttempted,
    Users.SignInOutcome Outcome,
    string? IpAddress,
    string? UserAgent,
    string? TraceId);

public sealed record SignInEventPage(
    IReadOnlyList<SignInEventView> Items, int TotalCount, int Page);

/// <summary>An account currently serving a lockout (ADR 0008).</summary>
public sealed record LockedOutUserView(Guid UserId, string Username, DateTimeOffset LockedOutUntil);

/// <summary>
/// Who is about. "Online" means seen within a window, because a cookie session has no logout
/// event to key off — the user who closed their browser looks identical to the one reading.
/// </summary>
public sealed record ActiveUsersView(int Count, TimeSpan Window);

/// <summary>
/// One name's traffic over a window: how much, how much of it failed, and how slow.
/// </summary>
/// <remarks>
/// The percentiles are interpolated from summed histogram buckets, not derived from
/// <see cref="MeanMs"/> — a mean cannot give a percentile, and averaging per-pod means would be
/// wrong even if it could. Null when the window holds no measurements. See ADR 0021.
/// </remarks>
public sealed record TrafficRowView(
    Abstractions.TrafficKind Kind,
    string Name,
    long Total,
    long Failed,
    long Faulted,
    double? MeanMs,
    double? P50Ms,
    double? P95Ms,
    double? P99Ms);

/// <summary>Traffic over a window, whole and per name.</summary>
public sealed record TrafficSummaryView(
    DateTimeOffset Since,
    double RequestsPerMinute,
    double ErrorRate,
    TrafficRowView Overall,
    IReadOnlyList<TrafficRowView> Rows);

/// <summary>One minute of the whole application's traffic, for the chart.</summary>
public sealed record TrafficPointView(
    DateTimeOffset BucketStart, long Total, long Failed, long Faulted, double? P95Ms);

public sealed record TrafficSeriesView(
    DateTimeOffset Since, IReadOnlyList<TrafficPointView> Points);
