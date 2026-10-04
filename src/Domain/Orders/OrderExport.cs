using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Orders;

/// <summary>
/// A CSV of one user's orders, built by a job in the worker. Requested, then Ready; a request that
/// never finishes reads as failed once <see cref="StaleAfter"/> has passed. ADR 0029.
/// </summary>
public sealed class OrderExport : Entity
{
    /// <summary>
    /// How long a request may stay unfinished before it reads as failed. It must outlast the
    /// worker's job retry schedule (1, 5 and 30 minutes, then the dead-letter queue): any shorter
    /// and the screen would say Failed while a retry was still coming, and asking again would start
    /// a second build beside it. Infrastructure.Tests pins the two together.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(45);

    private OrderExport(Guid id, Guid userId, DateTimeOffset requestedAt)
    {
        Id = id;
        UserId = userId;
        RequestedAt = requestedAt;
        Status = OrderExportStatus.Requested;
    }

    public Guid Id { get; private set; }

    /// <summary>Whose orders these are, and the only user who may download them (ADR 0007).</summary>
    public Guid UserId { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public OrderExportStatus Status { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public int? RowCount { get; private set; }

    /// <summary>The CSV text. Null until <see cref="Complete"/>.</summary>
    public string? Content { get; private set; }

    public static OrderExport Request(Guid id, Guid userId, DateTimeOffset requestedAt)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException("An order export needs a user.");
        }

        var export = new OrderExport(id, userId, requestedAt);
        export.Raise(new OrderExportRequested(id, userId));
        return export;
    }

    /// <summary>
    /// Requested -> Ready. On an export that is already Ready this does nothing and raises
    /// nothing: build jobs are delivered at least once, and a second delivery must neither replace
    /// the file nor send the user a second "ready" notification.
    /// </summary>
    public void Complete(string content, int rowCount, DateTimeOffset completedAt)
    {
        if (content is null)
        {
            throw new DomainException("A completed order export needs its file.");
        }

        if (rowCount < 0)
        {
            throw new DomainException("An order export cannot hold a negative number of orders.");
        }

        if (Status == OrderExportStatus.Ready)
        {
            return;
        }

        Status = OrderExportStatus.Ready;
        Content = content;
        RowCount = rowCount;
        CompletedAt = completedAt;
        Raise(new OrderExportCompleted(Id, UserId, rowCount));
    }

    /// <summary>Still being built: requested, and not yet past <see cref="StaleAfter"/>.</summary>
    public bool IsInProgress(DateTimeOffset now) =>
        Status == OrderExportStatus.Requested && !IsStale(Status, RequestedAt, now);

    /// <summary>Requested, and past <see cref="StaleAfter"/>: the build job has given up.</summary>
    public bool IsFailed(DateTimeOffset now) => IsStale(Status, RequestedAt, now);

    /// <summary>
    /// The one definition of "failed", shared with queries that read the status and the request
    /// time without loading the aggregate.
    /// </summary>
    public static bool IsStale(OrderExportStatus status, DateTimeOffset requestedAt, DateTimeOffset now) =>
        status == OrderExportStatus.Requested && now - requestedAt >= StaleAfter;
}
