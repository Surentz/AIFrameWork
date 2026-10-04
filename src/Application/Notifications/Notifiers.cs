using System.Globalization;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Notifications;
using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;

namespace AiFramework.Application.Notifications;

/// <summary>
/// The shared half of every notifier: resolve recipients, drop the ones this message already
/// notified, write the rest.
/// </summary>
/// <remarks>
/// A plain static helper rather than an abstract base class. The handlers are <c>sealed</c> with
/// primary constructors like everything else in this layer, and there is no inheritance anywhere
/// in <c>src/Application</c> — see root CLAUDE.md on why a base class was rejected for logging
/// for the same reason.
/// </remarks>
internal static class NotificationFanOut
{
    /// <summary>
    /// Writes one notification per recipient that has not already had one from this message.
    /// </summary>
    /// <returns>How many were actually written.</returns>
    public static async Task<int> WriteAsync(
        INotificationRepository notifications,
        IUnitOfWork unitOfWork,
        IEnumerable<INotificationPush> push,
        IClock clock,
        DomainEventContext context,
        IReadOnlyCollection<Guid> recipients,
        NotificationKind kind,
        string title,
        string body,
        Guid? subjectId,
        CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            return 0;
        }

        var alreadyNotified = await notifications
            .ListNotifiedRecipientsAsync(context.MessageId, kind, cancellationToken)
            .ConfigureAwait(false);

        var created = await AddForEachAsync(
                notifications,
                recipients.Where(r => !alreadyNotified.Contains(r)),
                context.MessageId,
                kind,
                title,
                body,
                subjectId,
                clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);

        var written = created.Count;

        // Everyone was already notified by an earlier delivery of this message. Nothing to
        // commit, and nothing to announce.
        if (written == 0)
        {
            return 0;
        }

        // Saved HERE, explicitly, and this is the part that is easy to get wrong: a domain event
        // handler runs on the outbox pump, NOT through the command pipeline, so the unit-of-work
        // behavior that normally commits exactly once after a command never runs. Without this
        // line every notification is added to a DbContext that is disposed with the scope and
        // nothing is ever written — silently, with the outbox row still marked Processed. Caught
        // by the integration tests finding an empty feed after a drain.
        //
        // Once for the whole batch rather than per recipient, so a fan-out is one round trip.
        // Failing here fails the message, which the outbox retries; the dedupe check above plus
        // the unique index make that retry a no-op for anyone already written.
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // AFTER the commit, never before: a client told about a notification that then failed to
        // commit would fetch the feed and not find it. Best-effort by contract (see
        // INotificationPush) - the row is already durable, so nothing here may fail the message.
        await PushAllAsync(push, created, cancellationToken).ConfigureAwait(false);

        return written;
    }

    /// <summary>
    /// Creates and tracks one notification per recipient. Split out of
    /// <see cref="WriteAsync"/> to keep it under MA0051's line limit — the rule is satisfied
    /// rather than suppressed, matching ObservabilityRegistration's own split.
    /// </summary>
    private static async Task<IReadOnlyList<Notification>> AddForEachAsync(
        INotificationRepository notifications,
        IEnumerable<Guid> recipients,
        Guid sourceMessageId,
        NotificationKind kind,
        string title,
        string body,
        Guid? subjectId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var created = new List<Notification>();

        foreach (var recipient in recipients)
        {
            var notification = Notification.Create(
                Guid.NewGuid(),
                recipient,
                sourceMessageId,
                kind,
                title,
                body,
                subjectId,
                createdAt);

            await notifications.AddAsync(notification, cancellationToken).ConfigureAwait(false);
            created.Add(notification);
        }

        return created;
    }

    private static async Task PushAllAsync(
        IEnumerable<INotificationPush> push,
        IReadOnlyList<Notification> created,
        CancellationToken cancellationToken)
    {
        foreach (var transport in push)
        {
            foreach (var notification in created)
            {
                var item = new NotificationListItem(
                    notification.Id,
                    notification.Kind,
                    notification.Title,
                    notification.Body,
                    notification.SubjectId,
                    notification.CreatedAt,
                    notification.ReadAt);

                await transport
                    .PushAsync(notification.UserId, item, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Notifies the buyer that their order was accepted.
/// </summary>
/// <remarks>
/// Named <c>Notifier</c>, not <c>NotificationHandler</c>, to stay clearly distinct from
/// <c>Infrastructure.EventPath.OrderPlacedNotificationHandler</c> — an unrelated type belonging
/// to the Wolverine spike (ADR 0005) that happens to sit on the same event.
/// </remarks>
public sealed class OrderPlacedNotifier(
    INotificationRepository notifications,
    IOrderRepository orders,
    IUnitOfWork unitOfWork,
    IEnumerable<INotificationPush> push,
    IClock clock)
    : IDomainEventHandler<OrderPlaced>
{
    public async Task HandleAsync(
        OrderPlaced domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // OrderPlaced carries no UserId, so the recipient is looked up — see IOrderRepository's
        // GetOwnerAsync for why that method is the one unscoped read on that port.
        var owner = await orders
            .GetOwnerAsync(domainEvent.OrderId, cancellationToken)
            .ConfigureAwait(false);

        // The order was deleted between the event being raised and delivered. Nothing to notify
        // anyone about, and throwing would retry until the message dead-letters over a row that
        // is never coming back.
        if (owner is not { } userId)
        {
            return;
        }

        await NotificationFanOut.WriteAsync(
            notifications,
            unitOfWork,
            push,
            clock,
            context,
            [userId],
            NotificationKind.OrderPlaced,
            "Order placed",
            $"We have your order for {domainEvent.Quantity} × {domainEvent.Sku}.",
            domainEvent.OrderId,
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class OrderShippedNotifier(
    INotificationRepository notifications,
    IUnitOfWork unitOfWork,
    IEnumerable<INotificationPush> push,
    IClock clock)
    : IDomainEventHandler<OrderShipped>
{
    public Task HandleAsync(
        OrderShipped domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // No lookup: this event carries its own recipient. Returned rather than awaited — there
        // is nothing after it, so an async state machine here would buy nothing (AsyncFixer01),
        // the same shape OrderPlacedAuditHandler already uses.
        return NotificationFanOut.WriteAsync(
            notifications,
            unitOfWork,
            push,
            clock,
            context,
            [domainEvent.UserId],
            NotificationKind.OrderShipped,
            "Order shipped",
            $"Your order for {domainEvent.Sku} is on its way.",
            domainEvent.OrderId,
            cancellationToken);
    }
}

public sealed class OrderCancelledNotifier(
    INotificationRepository notifications,
    IUnitOfWork unitOfWork,
    IEnumerable<INotificationPush> push,
    IClock clock)
    : IDomainEventHandler<OrderCancelled>
{
    public Task HandleAsync(
        OrderCancelled domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // Returned rather than awaited, for the reason on OrderShippedNotifier.
        return NotificationFanOut.WriteAsync(
            notifications,
            unitOfWork,
            push,
            clock,
            context,
            [domainEvent.UserId],
            NotificationKind.OrderCancelled,
            "Order cancelled",
            $"Your order for {domainEvent.Sku} was cancelled. {domainEvent.Reason}",
            domainEvent.OrderId,
            cancellationToken);
    }
}

/// <summary>
/// Notifies everyone who has bought a product that its price moved. The only notifier whose
/// recipients are not "the one person this happened to", and therefore the only one that needs a
/// rule for who receives it: past purchasers, bounded — see
/// <see cref="IOrderRepository.ListPurchaserIdsAsync"/>.
/// </summary>
public sealed class ProductPriceChangedNotifier(
    INotificationRepository notifications,
    IOrderRepository orders,
    IUnitOfWork unitOfWork,
    IEnumerable<INotificationPush> push,
    IClock clock)
    : IDomainEventHandler<ProductPriceChanged>
{
    /// <summary>
    /// The fan-out ceiling. Sized to stay a single ordinary transaction rather than to cover
    /// every conceivable catalogue — see <see cref="IOrderRepository.ListPurchaserIdsAsync"/> for
    /// what is traded away.
    /// </summary>
    public const int MaxRecipients = 500;

    public async Task HandleAsync(
        ProductPriceChanged domainEvent,
        DomainEventContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var recipients = await orders
            .ListPurchaserIdsAsync(domainEvent.Sku, MaxRecipients, cancellationToken)
            .ConfigureAwait(false);

        // InvariantCulture, explicitly: there is no localization anywhere in this application, and
        // a notification body is persisted once and read back forever, so formatting it under
        // whatever culture the pump's thread happened to carry would make stored text depend on
        // the machine that wrote it.
        var oldPrice = domainEvent.OldPrice.ToString("0.00", CultureInfo.InvariantCulture);
        var newPrice = domainEvent.NewPrice.ToString("0.00", CultureInfo.InvariantCulture);
        var direction = domainEvent.NewPrice < domainEvent.OldPrice ? "dropped" : "rose";

        await NotificationFanOut.WriteAsync(
            notifications,
            unitOfWork,
            push,
            clock,
            context,
            recipients,
            NotificationKind.ProductPriceChanged,
            "Price changed",
            $"{domainEvent.Name} {direction} from {oldPrice} to {newPrice}.",
            domainEvent.ProductId,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Tells the owner their export is built. Raised in the worker and delivered here, on an API
/// replica's outbox pump, because only the API can push (ADR 0028) — this is the notifier that ADR
/// was written for.
/// </summary>
public sealed class OrderExportReadyNotifier(
    INotificationRepository notifications,
    IUnitOfWork unitOfWork,
    IEnumerable<INotificationPush> push,
    IClock clock)
    : IDomainEventHandler<OrderExportCompleted>
{
    public Task HandleAsync(
        OrderExportCompleted domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var orders = domainEvent.RowCount == 1
            ? "1 order"
            : $"{domainEvent.RowCount.ToString(CultureInfo.InvariantCulture)} orders";

        // The subject is the export, not an order: the frontend's View link for this kind opens the
        // exports page, where the download is.
        return NotificationFanOut.WriteAsync(
            notifications,
            unitOfWork,
            push,
            clock,
            context,
            [domainEvent.UserId],
            NotificationKind.OrderExportReady,
            "Your order export is ready",
            $"{orders}, ready to download.",
            domainEvent.ExportId,
            cancellationToken);
    }
}
