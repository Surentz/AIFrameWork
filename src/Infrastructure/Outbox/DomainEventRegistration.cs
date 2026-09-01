using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AiFramework.Application.Abstractions;
using AiFramework.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>The JSON settings the outbox reads and writes with. One place, so they cannot drift.</summary>
public static class OutboxJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// An event's dispatch closure, captured at registration so dispatch needs no reflection —
/// the same technique AddCommand uses on the request path.
/// </summary>
public sealed record DomainEventDescriptor(
    string Name,
    Type EventType,
    Func<IServiceProvider, string, DomainEventContext, CancellationToken, Task> Dispatch);

public static class DomainEventRegistration
{
    /// <summary>
    /// Registers an event under a STABLE STRING NAME. The name, not the CLR type name, is what
    /// the outbox stores — so renaming the record cannot orphan unprocessed rows.
    /// </summary>
    public static IServiceCollection AddDomainEvent<TEvent>(this IServiceCollection services, string name)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        static async Task DispatchAsync(
            IServiceProvider sp, string json, DomainEventContext context, CancellationToken ct)
        {
            var domainEvent = JsonSerializer.Deserialize<TEvent>(json, OutboxJson.Options)
                ?? throw new InvalidOperationException(
                    $"Payload for '{typeof(TEvent).Name}' deserialised to null.");

            foreach (var handler in sp.GetServices<IDomainEventHandler<TEvent>>())
            {
                await handler.HandleAsync(domainEvent, context, ct).ConfigureAwait(false);
            }
        }

        return services.AddSingleton(new DomainEventDescriptor(name, typeof(TEvent), DispatchAsync));
    }
}

/// <summary>Name-to-descriptor lookup, built once. Singleton, like CommandRegistry.</summary>
public sealed class DomainEventRegistry
{
    private readonly Dictionary<string, DomainEventDescriptor> _byName;
    private readonly Dictionary<Type, DomainEventDescriptor> _byType;

    public DomainEventRegistry(IEnumerable<DomainEventDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var all = descriptors.ToArray();

        _byName = all.GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single()
                : throw new InvalidOperationException(
                    $"Domain event name '{g.Key}' is registered {g.Count()} times."),
                StringComparer.Ordinal);

        _byType = all.GroupBy(d => d.EventType)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single()
                : throw new InvalidOperationException(
                    $"Domain event '{g.Key.Name}' is registered {g.Count()} times."));
    }

    public bool TryGet(string name, [MaybeNullWhen(false)] out DomainEventDescriptor descriptor) =>
        _byName.TryGetValue(name, out descriptor);

    /// <summary>Used by the interceptor. Throws loudly rather than dropping an event silently.</summary>
    public string GetName(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return _byType.TryGetValue(eventType, out var descriptor)
            ? descriptor.Name
            : throw new InvalidOperationException(
                $"Domain event '{eventType.Name}' has no AddDomainEvent<T>(name) registration. " +
                "Add one in the composition root, or the event cannot be persisted.");
    }
}
