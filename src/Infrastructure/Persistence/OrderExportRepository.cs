using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class OrderExportRepository(AiFrameworkDbContext context) : IOrderExportRepository
{
    public async Task AddAsync(OrderExport export, CancellationToken cancellationToken) =>
        await context.OrderExports.AddAsync(export, cancellationToken).ConfigureAwait(false);

    public Task<OrderExport?> GetInProgressAsync(
        Guid ownerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The SQL form of OrderExport.IsInProgress: Requested, and younger than StaleAfter.
        var staleBefore = now - OrderExport.StaleAfter;
        return context.OrderExports.AsNoTracking()
            .Where(e => e.UserId == ownerId
                && e.Status == OrderExportStatus.Requested
                && e.RequestedAt > staleBefore)
            .OrderByDescending(e => e.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Projected, like ListAsync, so the SELECT never names content.
    public Task<OrderExportSummary?> GetSummaryAsync(
        Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        context.OrderExports.AsNoTracking()
            .Where(e => e.Id == id && e.UserId == ownerId)
            .Select(e => new OrderExportSummary(e.Id, e.Status, e.RequestedAt, e.CompletedAt, e.RowCount))
            .FirstOrDefaultAsync(cancellationToken);

    // Tracked deliberately — its only caller completes the export, and the unit-of-work behavior
    // has to find a tracked entity to save.
    public Task<OrderExport?> GetForUpdateAsync(
        Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        context.OrderExports
            .FirstOrDefaultAsync(e => e.Id == id && e.UserId == ownerId, cancellationToken);

    // Projected, so the SELECT names every column but content.
    public async Task<IReadOnlyList<OrderExportSummary>> ListAsync(
        Guid ownerId, CancellationToken cancellationToken) =>
        await context.OrderExports.AsNoTracking()
            .Where(e => e.UserId == ownerId)
            .OrderByDescending(e => e.RequestedAt)
            .Select(e => new OrderExportSummary(e.Id, e.Status, e.RequestedAt, e.CompletedAt, e.RowCount))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<OrderExportFile?> GetFileAsync(
        Guid id, Guid ownerId, CancellationToken cancellationToken) =>
        context.OrderExports.AsNoTracking()
            .Where(e => e.Id == id
                && e.UserId == ownerId
                && e.Status == OrderExportStatus.Ready
                && e.Content != null)
            // ! is safe: the Where above admits only rows whose Content is not null.
            .Select(e => new OrderExportFile(e.Content!, e.RequestedAt))
            .FirstOrDefaultAsync(cancellationToken);
}
