using AiFramework.Application.Orders;
using Microsoft.Extensions.Logging;

namespace AiFramework.Infrastructure.Jobs;

/// <summary>
/// The seam where a real mail provider goes. Logs today, deliberately and visibly.
/// </summary>
/// <remarks>
/// <para>
/// This repository has no mail provider and adding one is not what ADR 0016 is about, so the
/// adapter records the intent rather than pretending to send. It is a real port with a real
/// implementation — swapping in SendGrid or SES is a change to this one class and a package
/// reference, with no caller affected.
/// </para>
/// <para>
/// <b>Idempotency lives here when it arrives.</b> Domain event delivery is at-least-once, so
/// <c>OrderPlacedConfirmationHandler</c> enqueues this job more than once for the same order at
/// some point. A real provider needs a dedupe key — the order id, or the
/// <c>DomainEventContext.MessageId</c> that handler could pass through — exactly as
/// <c>OrderAuditWriter</c> keys its row. Logging twice is harmless; emailing twice is not.
/// </para>
/// </remarks>
public sealed partial class LoggingOrderNotifier(ILogger<LoggingOrderNotifier> logger)
    : IOrderNotifier
{
    public Task SendOrderConfirmationAsync(
        Guid orderId, string sku, int quantity, CancellationToken cancellationToken)
    {
        LogConfirmation(logger, orderId, quantity, sku);
        return Task.CompletedTask;
    }

    // Information, not Debug: unlike a dispatch outcome — which Behaviors.LoggedAsync already
    // reports and root CLAUDE.md keeps at Debug — this records that a customer-visible side
    // effect was intended, which is the kind of thing an operator goes looking for.
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Order confirmation for {OrderId}: {Quantity} x {Sku}. No mail provider is configured.")]
    private static partial void LogConfirmation(ILogger logger, Guid orderId, int quantity, string sku);
}
