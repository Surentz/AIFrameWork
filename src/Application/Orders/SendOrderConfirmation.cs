using AiFramework.Application.Abstractions;
using AiFramework.Domain.Orders;

namespace AiFramework.Application.Orders;

/// <summary>Tells the customer their order was placed. Infrastructure owns how.</summary>
public interface IOrderNotifier
{
    public Task SendOrderConfirmationAsync(
        Guid orderId, string sku, int quantity, CancellationToken cancellationToken);
}

/// <summary>
/// The reference LIGHT job: one outbound call, negligible CPU, measured in milliseconds.
/// </summary>
/// <remarks>
/// Carries the order's details rather than just its id, so the handler needs no database read —
/// a light job that opens a connection is not a light job. It also sidesteps ownership entirely:
/// <c>IOrderRepository</c> reads are scoped to an owner (ADR 0007) and <c>OrderPlaced</c> does not
/// carry one, so a handler that looked the order up would need a user this job has no business
/// knowing. Contrast <c>BuildOrderExport</c>, which genuinely does act for a user and therefore
/// carries one.
/// </remarks>
public sealed record SendOrderConfirmation(Guid OrderId, string Sku, int Quantity) : IJob
{
    public static JobLane Lane => JobLane.Light;
}

public sealed class SendOrderConfirmationHandler(IOrderNotifier notifier)
{
    public Task Handle(SendOrderConfirmation job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        return notifier.SendOrderConfirmationAsync(
            job.OrderId, job.Sku, job.Quantity, cancellationToken);
    }
}

/// <summary>
/// <b>This is the reference for enqueuing a job that must not be lost.</b>
/// </summary>
/// <remarks>
/// <para>
/// <c>PlaceOrderHandler</c> does NOT call <see cref="IJobScheduler"/> itself, and that is the whole
/// point. An enqueue from a command handler publishes immediately, so a command whose transaction
/// then fails would still have sent the customer a confirmation for an order that does not exist.
/// </para>
/// <para>
/// Going through the domain event instead makes the job exist if and only if the order committed:
/// <c>DomainEventsInterceptor</c> writes the outbox row in the same <c>SaveChangesAsync</c> as the
/// aggregate. See <see cref="IJobScheduler"/>'s remarks for why Wolverine's own EF Core outbox could
/// not be used for this, and ADR 0016.
/// </para>
/// <para>
/// Delivery here is at-least-once, so this handler runs twice at some point and enqueues the job
/// twice. That is deliberate at this layer — the dedupe key is
/// <c>DomainEventContext.MessageId</c>, and making the SEND idempotent is the notifier
/// implementation's job, exactly as <c>OrderPlacedAuditHandler</c> keys its audit row on the same
/// value.
/// </para>
/// </remarks>
public sealed class OrderPlacedConfirmationHandler(IJobScheduler jobs)
    : IDomainEventHandler<OrderPlaced>
{
    public Task HandleAsync(
        OrderPlaced domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return jobs.EnqueueAsync(
            new SendOrderConfirmation(domainEvent.OrderId, domainEvent.Sku, domainEvent.Quantity),
            cancellationToken);
    }
}
