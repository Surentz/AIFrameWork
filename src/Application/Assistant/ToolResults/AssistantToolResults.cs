namespace AiFramework.Application.Assistant.ToolResults;

// What each assistant tool returns, and therefore what is sent to the model provider. Separate
// types from the monitoring views on purpose (ADR 0033): a field added to a view reaches the
// model only when someone adds it here too, where AssistantToolResultsBoundaryTests reads it.
// Nothing else may live in this namespace.

public sealed record JobHealthResult(int Running, int Succeeded, int Failed, int DeadLettered, DateTimeOffset Since);

public sealed record JobRunResult(
    Guid EnvelopeId,
    int Attempt,
    string JobName,
    string? Lane,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long? DurationMs,
    string? Error,
    string? TraceId);

public sealed record JobRunsResult(IReadOnlyList<JobRunResult> Items, int TotalCount, int Page);

public sealed record DeadLetterResult(
    Guid Id, string MessageType, string ExceptionType, string? ExceptionMessage, DateTimeOffset SentAt, bool Replayable);

public sealed record DeadLettersResult(IReadOnlyList<DeadLetterResult> Items, int TotalCount, int Page);

public sealed record TrafficRowResult(
    string Kind, string Name, long Total, long Failed, long Faulted, double? P50Ms, double? P95Ms, double? P99Ms);

public sealed record TrafficSummaryResult(
    DateTimeOffset Since, double RequestsPerMinute, double ErrorRate, TrafficRowResult Overall, IReadOnlyList<TrafficRowResult> Rows);

public sealed record TrafficPointResult(DateTimeOffset BucketStart, long Total, long Failed, long Faulted, double? P95Ms);

public sealed record TrafficSeriesResult(DateTimeOffset Since, IReadOnlyList<TrafficPointResult> Points);

public sealed record ExternalSystemResult(
    string Name,
    string? State,
    string? Description,
    DateTimeOffset? CheckedAt,
    bool Stale,
    DateTimeOffset? CertificateNotAfter,
    bool? TokenOk,
    long Calls,
    long Failed,
    long Faulted,
    long Attempts,
    double? P95Ms);

public sealed record ExternalSystemsResult(DateTimeOffset TrafficSince, IReadOnlyList<ExternalSystemResult> Systems);

public sealed record SignInHealthResult(
    int Succeeded, int BadCredentials, int LockedOut, int UnknownUser, DateTimeOffset Since, int ActiveUsers, int LockedOutAccounts);

public sealed record OrderResult(
    Guid Id, string Sku, string? ProductName, int Quantity, decimal? UnitPrice, string Status, DateTimeOffset PlacedAt);

public sealed record OrdersResult(IReadOnlyList<OrderResult> Items, string? NextCursor);

public sealed record OrderDetailResult(
    Guid Id,
    string Sku,
    string? ProductName,
    int Quantity,
    decimal? UnitPrice,
    string Status,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? CancelledAt);

public sealed record ExportResult(Guid Id, string Status, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt, int? RowCount);

public sealed record ExportsResult(IReadOnlyList<ExportResult> Items);

public sealed record ProductResult(Guid Id, string Sku, string Name, decimal Price);

public sealed record ProductsResult(IReadOnlyList<ProductResult> Items, string? NextCursor);

public sealed record PopulationResult(string AreaCode, string AreaName, string Period, long Population, string Source);
