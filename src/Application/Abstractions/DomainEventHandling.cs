using AiFramework.Domain.Abstractions;

namespace AiFramework.Application.Abstractions;

/// <summary>
/// What a handler needs to be idempotent. MessageId is stable across every redelivery of the
/// same event, so it is the dedupe key — "have I already processed message X?" is answerable
/// without inventing a business key. Attempt lets a handler degrade on a retry.
/// </summary>
#pragma warning disable MA0008 // DomainEventContext is an immutable value type passed frequently; StructLayoutAttribute is unnecessary
public readonly record struct DomainEventContext(Guid MessageId, int Attempt);
#pragma warning restore MA0008

/// <summary>
/// Handles one domain event. MUST be idempotent: delivery is at-least-once, and retry
/// granularity is the message rather than the handler, so a partially-failed fan-out
/// re-runs the handlers that already succeeded.
/// </summary>
#pragma warning disable CA1711 // IDomainEventHandler naming is intentional—the interface IS a domain event handler
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    public Task HandleAsync(TEvent domainEvent, DomainEventContext context, CancellationToken cancellationToken);
}
#pragma warning restore CA1711
