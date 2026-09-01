namespace AiFramework.Domain.Abstractions;

/// <summary>
/// Marker for something that happened in the domain. Deliberately carries no timestamp:
/// Domain has no clock, and the outbox row's OccurredAt is stamped by the interceptor,
/// which can inject IClock. Events stay pure data and trivially testable.
/// </summary>
public interface IDomainEvent;

/// <summary>An aggregate root that collects the events it raises until they are persisted.</summary>
public abstract class Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// <summary>
    /// Public because the SaveChanges interceptor, in another assembly, must call it after
    /// copying the events to the outbox. The alternative — internal plus InternalsVisibleTo —
    /// would create a compile-time coupling between Domain and Infrastructure that the
    /// dependency rule exists to prevent.
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}
