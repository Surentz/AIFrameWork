using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Orders;

/// <summary>A user asked for a file of their orders. Its handler enqueues the build job.</summary>
public sealed record OrderExportRequested(Guid ExportId, Guid UserId) : IDomainEvent;

/// <summary>
/// The file is built and stored. Raised in the worker; an API replica delivers it, which is where
/// the user is notified and pushed (ADR 0028).
/// </summary>
public sealed record OrderExportCompleted(Guid ExportId, Guid UserId, int RowCount) : IDomainEvent;
