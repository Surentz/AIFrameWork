using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

/// <summary>An export as the list shows it: everything but the file.</summary>
public sealed record OrderExportSummary(
    Guid Id,
    OrderExportStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    int? RowCount);

/// <summary>A Ready export's file, and when it was asked for (which names the download).</summary>
public sealed record OrderExportFile(string Content, DateTimeOffset RequestedAt);

/// <summary>
/// Every read is scoped to the owner by signature, as <see cref="IOrderRepository"/> is: there is
/// no overload that reaches another user's export, so a caller cannot forget to filter (ADR 0007).
/// </summary>
public interface IOrderExportRepository
{
    public Task AddAsync(OrderExport export, CancellationToken cancellationToken);

    /// <summary>
    /// The owner's newest export that is still being built — Requested and younger than
    /// <see cref="OrderExport.StaleAfter"/> at <paramref name="now"/> — or null. Untracked.
    /// </summary>
    public Task<OrderExport?> GetInProgressAsync(
        Guid ownerId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The status of one export, without the file. Null if missing or someone else's. The build job
    /// reads this on every delivery, a duplicate one included, so it must not load the CSV.
    /// </summary>
    public Task<OrderExportSummary?> GetSummaryAsync(
        Guid id, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>Tracked, for <see cref="OrderExport.Complete"/>. Null if missing or someone else's.</summary>
    public Task<OrderExport?> GetForUpdateAsync(
        Guid id, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// The owner's exports, newest first. Never loads the file: a list of seven days of exports
    /// must not drag every CSV along with it.
    /// </summary>
    public Task<IReadOnlyList<OrderExportSummary>> ListAsync(
        Guid ownerId, CancellationToken cancellationToken);

    /// <summary>The file of a Ready export the owner holds, or null — missing, someone else's, or not built.</summary>
    public Task<OrderExportFile?> GetFileAsync(
        Guid id, Guid ownerId, CancellationToken cancellationToken);
}

/// <summary>Deletes exports past the configured retention. ADR 0029.</summary>
public interface IOrderExportRetention
{
    public Task<int> PruneAsync(CancellationToken cancellationToken);
}
