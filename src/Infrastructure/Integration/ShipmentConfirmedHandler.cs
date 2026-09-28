using AiFramework.Application.Abstractions;
using AiFramework.Application.IntegrationEvents;
using AiFramework.Application.Orders;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace AiFramework.Infrastructure.Integration;

/// <summary>
/// An inbound message that can never succeed as sent: not found, cancelled, invalid. Its message
/// names the order so an operator can find it on the monitoring page's dead letters.
/// </summary>
public sealed class IntegrationMessageRejectedException(string message) : Exception(message);

/// <summary>
/// The worker's adapter for shipment.confirmed.v1: dispatches <see cref="RecordShipment"/> through
/// the same pipeline an HTTP request uses (validation, logging, the unit of work). Worker-only: the
/// API listens to no queue. ADR 0026.
/// </summary>
/// <remarks>
/// A malformed body never reaches this class: Wolverine fails to deserialize it, and the
/// <c>JsonException</c> dead-letters it at once, even under the worker's global ScheduleRetry
/// policy, while the queue behind it keeps moving (plan, Verified API V5b).
/// </remarks>
public sealed class ShipmentConfirmedHandler(ICommandDispatcher commands)
{
    /// <summary>
    /// Rejections go straight to the dead letters - retrying cannot change "not found". Anything
    /// else (the database unreachable) falls to the worker's global policy: ScheduleRetry 1/5/30
    /// minutes, then dead letters, the job lanes' shape.
    /// </summary>
    public static void Configure(HandlerChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        chain.OnException<IntegrationMessageRejectedException>().MoveToErrorQueue();
    }

    public async Task Handle(ShipmentConfirmedV1 message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var result = await commands.SendAsync(
            new RecordShipment(message.OrderId, message.ShipmentId, message.ShippedAt),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new IntegrationMessageRejectedException(
                $"shipment {message.ShipmentId} for order {message.OrderId} rejected: " +
                $"{result.Error.Code} - {result.Error.Message}");
        }
    }
}
