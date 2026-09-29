using System.Globalization;
using AiFramework.Application.IntegrationEvents;
using Wolverine;

namespace AiFramework.Infrastructure.Integration;

/// <summary>
/// The port over Wolverine. PublishAsync writes an envelope to the durable outbox (Postgres); the
/// sending agent delivers it to RabbitMQ and retries through outages. ADR 0026.
/// </summary>
public sealed class WolverineIntegrationEventPublisher(IMessageBus bus) : IIntegrationEventPublisher
{
    public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        // Checked, not forwarded: PublishAsync takes no token (JobScheduler records the same).
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        // Headers, not the message, carry what the mapper writes as message_id and type: they are
        // the only part of the envelope that survives recovery by another host. See
        // IntegrationEnvelopeMapper.
        var delivery = new DeliveryOptions()
            .WithHeader(IntegrationEnvelopeMapper.EventIdHeader, integrationEvent.EventId.ToString("D", CultureInfo.InvariantCulture))
            .WithHeader(IntegrationEnvelopeMapper.EventTypeHeader, TEvent.TypeName);

        return bus.PublishAsync(integrationEvent, delivery).AsTask();
    }
}
