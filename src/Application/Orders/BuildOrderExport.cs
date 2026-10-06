using System.Text;
using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Orders;

/// <summary>
/// Builds an export's CSV. The reference HEAVY job: it pages every order a user has — unbounded work
/// over an unbounded result set, exactly what must never run on a request thread. ADR 0029.
/// </summary>
/// <remarks>
/// <b>Carries <see cref="OwnerId"/> because there is no <c>HttpContext</c> in the worker.</b> Every
/// query and command below resolves the caller through <c>ICurrentUser</c>, and ADR 0007 puts
/// ownership in those reads, so there is no "read everything" mode to fall back to. The worker
/// populates <c>ICurrentUser</c> from this property before the handler runs — see
/// <c>Infrastructure/Jobs/JobCurrentUser.cs</c>.
/// </remarks>
public sealed record BuildOrderExport(Guid ExportId, Guid OwnerId) : IUserScopedJob
{
    public static JobLane Lane => JobLane.Heavy;
}

/// <summary>
/// Goes through <see cref="IQueryDispatcher"/> and <see cref="ICommandDispatcher"/> rather than the
/// repositories, which is the expected shape for any job that touches domain state: validation and
/// the logging behavior stay on the path, so the job's reads and its write are recorded exactly like
/// a request's. It logs nothing itself.
/// </summary>
public sealed class BuildOrderExportHandler(IQueryDispatcher queries, ICommandDispatcher commands)
{
    public async Task Handle(BuildOrderExport job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var export = await queries.SendAsync(new GetOrderExport(job.ExportId), cancellationToken).ConfigureAwait(false);

        // Gone (pruned) or already built: nothing to do. The second case is the normal one — a
        // redelivered OrderExportRequested enqueues this job twice, and only the first should work.
        // Returning is right for both: neither is a failure a retry could fix.
        //
        // NotFound ONLY. Any other failure throws, Unauthorized above all: that is what a missing
        // caller produces, so swallowing it would hide JobUserMiddleware failing to set the owner —
        // and JobDeliveryTests relies on this throwing to prove the middleware works.
        if (!export.IsSuccess)
        {
            if (export.Error.Kind == ErrorKind.NotFound)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Building order export {job.ExportId} failed reading the export: {export.Error.Code}.");
        }

        if (export.Value.Status == OrderExportState.Ready)
        {
            return;
        }

        var rows = new List<OrderExportRow>();
        string? cursor = null;
        do
        {
            var page = await queries
                .SendAsync(new GetOrderExportRows(GetOrderExportRows.MaxLimit, cursor), cancellationToken)
                .ConfigureAwait(false);

            if (!page.IsSuccess)
            {
                // Thrown, not swallowed: Wolverine's error policy decides whether to retry or
                // dead-letter, and it can only see an exception. A job that returns quietly on
                // failure looks identical to one that succeeded, in every log and every metric.
                throw new InvalidOperationException(
                    $"Building order export {job.ExportId} failed reading orders: {page.Error.Code}.");
            }

            rows.AddRange(page.Value.Items);
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        var completed = await commands
            .SendAsync(new CompleteOrderExport(job.ExportId, Encoding.UTF8.GetBytes(OrderExportCsv.Build(rows)), rows.Count), cancellationToken)
            .ConfigureAwait(false);

        if (!completed.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Building order export {job.ExportId} failed storing the file: {completed.Error.Code}.");
        }
    }
}

/// <summary>
/// Keeps <c>order_exports</c> to <c>OrderExports__RetentionDays</c> (seven by default). Each row is a
/// copy of someone's order history, so the sweep is a requirement of the feature, not housekeeping.
/// </summary>
public sealed record PruneOrderExports : IJob
{
    public static JobLane Lane => JobLane.Light;
}

public sealed class PruneOrderExportsHandler(IOrderExportRetention retention)
{
    public Task Handle(PruneOrderExports job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        return retention.PruneAsync(cancellationToken);
    }
}
