using System.Runtime.InteropServices;
using AiFramework.Domain.Abstractions;

namespace AiFramework.Application.Abstractions;

/// <summary>
/// What a handler needs to be idempotent. MessageId is stable across every redelivery of the
/// same event, so it is the dedupe key — "have I already processed message X?" is answerable
/// without inventing a business key. Attempt lets a handler degrade on a retry.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct DomainEventContext(Guid MessageId, int Attempt);

/// <summary>
/// Handles one domain event. MUST be idempotent: delivery is at-least-once, and retry
/// granularity is the message rather than the handler, so a partially-failed fan-out
/// re-runs the handlers that already succeeded.
/// </summary>
// CA1711: the name ends in "EventHandler", a suffix the rule reserves for delegate types.
// This is a handler interface in a CQRS/DDD vocabulary where the name is the domain term,
// not a delegate masquerading as something else, so the rule's intent does not apply here.
#pragma warning disable CA1711
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    public Task HandleAsync(TEvent domainEvent, DomainEventContext context, CancellationToken cancellationToken);
}
#pragma warning restore CA1711
