using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Orders;

// None of these is ICacheable, deliberately. The list must show Ready the moment the notification
// says so — a cached "Requested" for thirty seconds would contradict it — and a cached file would
// keep personal data in memory past the request that asked for it.

/// <summary>The caller's exports, newest first.</summary>
public sealed record GetOrderExports : IQuery<IReadOnlyList<OrderExportView>>;

public sealed class GetOrderExportsHandler(
    IOrderExportRepository exports, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetOrderExports, IReadOnlyList<OrderExportView>>
{
    public async Task<Result<IReadOnlyList<OrderExportView>>> HandleAsync(
        GetOrderExports query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<IReadOnlyList<OrderExportView>>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        var now = clock.UtcNow;
        var summaries = await exports.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<OrderExportView>>(
            [.. summaries.Select(s => OrderExportView.From(s, now))]);
    }
}

/// <summary>One of the caller's exports. The build job reads its status through this.</summary>
public sealed record GetOrderExport(Guid Id) : IQuery<OrderExportView>;

public sealed class GetOrderExportHandler(
    IOrderExportRepository exports, ICurrentUser currentUser, IClock clock)
    : IQueryHandler<GetOrderExport, OrderExportView>
{
    public async Task<Result<OrderExportView>> HandleAsync(GetOrderExport query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<OrderExportView>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        var export = await exports.GetSummaryAsync(query.Id, userId, cancellationToken).ConfigureAwait(false);
        return export is null
            ? Result.Failure<OrderExportView>(new Error(
                ErrorKind.NotFound, "order_exports.not_found", "That export does not exist."))
            : Result.Success(OrderExportView.From(export, clock.UtcNow));
    }
}

/// <summary>A Ready export's file, and the name it downloads as.</summary>
public sealed record OrderExportDownload(string FileName, byte[] Document);

public sealed record GetOrderExportFile(Guid Id) : IQuery<OrderExportDownload>;

/// <remarks>
/// Someone else's export and one that is not built yet answer the same NotFound, so an export id
/// reveals nothing about whose it is or whether it exists (ADR 0007).
/// </remarks>
public sealed class GetOrderExportFileHandler(IOrderExportRepository exports, ICurrentUser currentUser)
    : IQueryHandler<GetOrderExportFile, OrderExportDownload>
{
    public async Task<Result<OrderExportDownload>> HandleAsync(
        GetOrderExportFile query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<OrderExportDownload>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        var file = await exports.GetFileAsync(query.Id, userId, cancellationToken).ConfigureAwait(false);
        return file is null
            ? Result.Failure<OrderExportDownload>(new Error(
                ErrorKind.NotFound, "order_exports.not_found", "That export does not exist or is not ready."))
            : Result.Success(new OrderExportDownload(
                $"orders-{file.RequestedAt.UtcDateTime:yyyy-MM-dd}.pdf", file.Document));
    }
}

public sealed record OrderExportRowPage(IReadOnlyList<OrderExportRow> Items, string? NextCursor);

/// <summary>
/// One page of the caller's orders, with every column the export writes. Its own query rather than
/// GetOrders because OrderListItem has no shipped or cancelled fields, and with a larger page,
/// because only the build job asks.
/// </summary>
public sealed record GetOrderExportRows(int Limit, string? Cursor) : IQuery<OrderExportRowPage>
{
    public const int MaxLimit = 500;
}

/// <summary>
/// Owner-scoped exactly like <see cref="GetOrdersHandler"/>, through the same keyset paging: the
/// worker sets ICurrentUser to the export's owner, so this reads that user's orders and nobody else's.
/// </summary>
public sealed class GetOrderExportRowsHandler(IOrderRepository orders, ICurrentUser currentUser)
    : IQueryHandler<GetOrderExportRows, OrderExportRowPage>
{
    public async Task<Result<OrderExportRowPage>> HandleAsync(
        GetOrderExportRows query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<OrderExportRowPage>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        if (query.Limit is < 1 or > GetOrderExportRows.MaxLimit)
        {
            return Result.Failure<OrderExportRowPage>(new Error(
                ErrorKind.Validation, "order_exports.limit_out_of_range",
                $"Limit must be between 1 and {GetOrderExportRows.MaxLimit}."));
        }

        (DateTimeOffset PlacedAt, Guid Id)? after = null;
        if (query.Cursor is not null)
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var decoded))
            {
                return Result.Failure<OrderExportRowPage>(new Error(
                    ErrorKind.Validation, "order_exports.malformed_cursor", "The cursor could not be parsed."));
            }

            after = decoded;
        }

        // One more than asked for, so "is there a next page" needs no second COUNT.
        var rows = await orders.ListAsync(userId, query.Limit + 1, after, cancellationToken).ConfigureAwait(false);

        var page = rows.Take(query.Limit)
            .Select(o => new OrderExportRow(
                o.Id, o.Sku, o.Product?.Name, o.Quantity, o.Product?.UnitPrice, o.Status,
                o.PlacedAt, o.ShippedAt, o.CancelledAt, o.CancellationReason))
            .ToArray();

        var next = rows.Count > query.Limit ? KeysetCursor.Encode(page[^1].PlacedAt, page[^1].OrderId) : null;
        return Result.Success(new OrderExportRowPage(page, next));
    }
}
