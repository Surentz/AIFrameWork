namespace AiFramework.Application.IntegrationEvents;

/// <summary>
/// Publishes an integration event to other systems. Infrastructure decides how - today a
/// durable Wolverine outbox to RabbitMQ (ADR 0026) - so this layer references neither.
/// </summary>
public interface IIntegrationEventPublisher
{
    public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent;
}
