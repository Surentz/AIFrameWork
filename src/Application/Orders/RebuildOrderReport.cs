using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Orders;

/// <summary>Where a finished report goes. Infrastructure owns the destination.</summary>
public interface IOrderReportWriter
{
    public Task WriteAsync(
        Guid ownerId, int orderCount, int totalQuantity, CancellationToken cancellationToken);
}

/// <summary>
/// The reference HEAVY job: pages every order a user has, which is unbounded work over an
/// unbounded result set — exactly what must never run on a request thread.
/// </summary>
/// <remarks>
/// <b>Carries <see cref="OwnerId"/> because there is no <c>HttpContext</c> in the worker.</b>
/// <c>GetOrders</c> resolves the caller through <c>ICurrentUser</c> and fails
/// <c>Unauthorized</c> without one (ADR 0007 puts ownership in the query, so there is no "read
/// everything" mode to fall back to). The worker populates <c>ICurrentUser</c> from this property
/// before the handler runs — see <c>Infrastructure/Jobs/JobCurrentUser.cs</c>.
/// </remarks>
public sealed record RebuildOrderReport(Guid OwnerId) : IUserScopedJob
{
    public static JobLane Lane => JobLane.Heavy;
}

/// <summary>
/// Goes through <see cref="IQueryDispatcher"/> rather than touching <c>IOrderRepository</c>
/// directly, which is the expected shape for any job that reads domain state: it keeps the
/// logging behavior on the path, so the job's reads are recorded exactly like a request's.
/// </summary>
public sealed class RebuildOrderReportHandler(
    IQueryDispatcher queries, IOrderReportWriter writer)
{
    // One more than GetOrders' own MaxLimit would be rejected; this is the largest page it takes.
    private const int PageSize = 100;

    public async Task Handle(RebuildOrderReport job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var orderCount = 0;
        var totalQuantity = 0;
        string? cursor = null;

        do
        {
            var page = await queries
                .SendAsync(new GetOrders(PageSize, cursor), cancellationToken)
                .ConfigureAwait(false);

            if (!page.IsSuccess)
            {
                // Thrown, not swallowed: Wolverine's error policy decides whether to retry or
                // dead-letter, and it can only see an exception. A job that returns quietly on
                // failure looks identical to one that succeeded, in every log and every metric.
                throw new InvalidOperationException(
                    $"Rebuilding the order report for {job.OwnerId} failed: {page.Error.Code}.");
            }

            orderCount += page.Value.Items.Count;
            totalQuantity += page.Value.Items.Sum(item => item.Quantity);
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        await writer
            .WriteAsync(job.OwnerId, orderCount, totalQuantity, cancellationToken)
            .ConfigureAwait(false);
    }
}
