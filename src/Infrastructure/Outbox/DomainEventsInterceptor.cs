using AiFramework.Application.Abstractions;
using AiFramework.Domain.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>
/// Copies pending domain events onto the outbox as part of the SAME SaveChanges call, so EF
/// writes them in the aggregate's transaction. This must run in SavingChangesAsync, before the
/// save: in SavedChangesAsync the transaction has already committed and atomicity is gone.
/// </summary>
public sealed class DomainEventsInterceptor(DomainEventRegistry registry, IClock clock)
    : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        var context = eventData.Context;
        if (context is null)
        {
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        var entities = context.ChangeTracker.Entries<Entity>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToArray();

        var occurredAt = clock.UtcNow;

        foreach (var entity in entities)
        {
            foreach (var domainEvent in entity.DomainEvents)
            {
                context.Add(new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    EventName = registry.GetName(domainEvent.GetType()),
                    Payload = JsonSerializer.Serialize(
                        domainEvent, domainEvent.GetType(), OutboxJson.Options),
                    OccurredAt = occurredAt,
                    Status = OutboxStatus.Pending,
                });
            }

            entity.ClearDomainEvents();
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
