using AiFramework.Application.Abstractions;
using AiFramework.Domain.Orders;
using FluentValidation;

namespace AiFramework.Application.Orders;

/// <summary>
/// An export as the caller sees it. <see cref="OrderExportState.Failed"/> is never stored: it is a
/// Requested export past <see cref="OrderExport.StaleAfter"/>, read at the time of asking.
/// </summary>
public enum OrderExportState
{
    Requested,
    Ready,
    Failed,
}

public sealed record OrderExportView(
    Guid Id,
    OrderExportState Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    int? RowCount)
{
    public static OrderExportView From(OrderExportSummary summary, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new OrderExportView(
            summary.Id,
            State(summary.Status, summary.RequestedAt, now),
            summary.RequestedAt,
            summary.CompletedAt,
            summary.RowCount);
    }

    public static OrderExportView From(OrderExport export, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(export);

        return new OrderExportView(
            export.Id,
            State(export.Status, export.RequestedAt, now),
            export.RequestedAt,
            export.CompletedAt,
            export.RowCount);
    }

    private static OrderExportState State(OrderExportStatus status, DateTimeOffset requestedAt, DateTimeOffset now)
    {
        if (status == OrderExportStatus.Ready)
        {
            return OrderExportState.Ready;
        }

        return OrderExport.IsStale(status, requestedAt, now) ? OrderExportState.Failed : OrderExportState.Requested;
    }
}

/// <summary>
/// Asks for a file of the caller's orders. Not cacheable — a command — and with no input to
/// validate. A request while one is still being built returns that one rather than starting
/// another.
/// </summary>
/// <remarks>
/// Check-then-insert, deliberately without a lock: two requests that arrive within the same few
/// milliseconds (two tabs, two API replicas) can each see none in progress and start one. The page
/// disables its button while a request is pending, so it takes two windows to do it, and the worst
/// outcome is a second file and a second notification — not worth a per-user lock or a partial
/// unique index that a stale request would then have to be cleared out of. ADR 0029.
/// </remarks>
public sealed record RequestOrderExport : ICommand<OrderExportView>;

/// <remarks>
/// Adds the export and nothing else. Its OrderExportRequested event is written to the outbox in the
/// same transaction, and OrderExportJobEnqueuer enqueues the build off it — the repository's rule for
/// a job that must not be lost (root CLAUDE.md, "Jobs"). Enqueuing from here would publish before
/// the commit, so a failed commit would still run a build for an export that does not exist.
/// </remarks>
public sealed class RequestOrderExportHandler(
    IOrderExportRepository exports, ICurrentUser currentUser, IClock clock)
    : ICommandHandler<RequestOrderExport, OrderExportView>
{
    public async Task<Result<OrderExportView>> HandleAsync(
        RequestOrderExport command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<OrderExportView>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        var now = clock.UtcNow;
        var inProgress = await exports.GetInProgressAsync(userId, now, cancellationToken).ConfigureAwait(false);
        if (inProgress is not null)
        {
            return Result.Success(OrderExportView.From(inProgress, now));
        }

        var export = OrderExport.Request(Guid.NewGuid(), userId, now);
        await exports.AddAsync(export, cancellationToken).ConfigureAwait(false);
        return Result.Success(OrderExportView.From(export, now));
    }
}

/// <summary>
/// Stores a built file. Dispatched by the build job in the worker, where ICurrentUser is the
/// export's owner (IUserScopedJob), so the owner scoping below holds there as it does in the API.
/// </summary>
public sealed record CompleteOrderExport(Guid ExportId, byte[] Document, int RowCount) : ICommand<bool>;

public sealed class CompleteOrderExportValidator : AbstractValidator<CompleteOrderExport>
{
    public CompleteOrderExportValidator()
    {
        RuleFor(c => c.ExportId).NotEmpty();
        RuleFor(c => c.Document).NotEmpty();
        RuleFor(c => c.RowCount).GreaterThanOrEqualTo(0);
    }
}

public sealed class CompleteOrderExportHandler(
    IOrderExportRepository exports, ICurrentUser currentUser, IClock clock)
    : ICommandHandler<CompleteOrderExport, bool>
{
    public async Task<Result<bool>> HandleAsync(CompleteOrderExport command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<bool>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        // Tracked: Complete mutates it, and an untracked read would lose the write silently.
        var export = await exports
            .GetForUpdateAsync(command.ExportId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (export is null)
        {
            return Result.Failure<bool>(new Error(
                ErrorKind.NotFound, "order_exports.not_found", "That export does not exist."));
        }

        // A no-op on an export that is already Ready: a redelivered build lands here twice.
        export.Complete(command.Document, command.RowCount, clock.UtcNow);
        return Result.Success(true);
    }
}

/// <summary>Enqueues the build when an export is requested. Runs on the API's outbox pump.</summary>
/// <remarks>
/// At-least-once, like every domain event handler: a redelivery enqueues the build twice. The job
/// absorbs that — it stops before doing any work once the export is Ready.
/// </remarks>
public sealed class OrderExportJobEnqueuer(IJobScheduler jobs) : IDomainEventHandler<OrderExportRequested>
{
    public Task HandleAsync(
        OrderExportRequested domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return jobs.EnqueueAsync(new BuildOrderExport(domainEvent.ExportId, domainEvent.UserId), cancellationToken);
    }
}
