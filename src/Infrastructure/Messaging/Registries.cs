using System.Diagnostics.CodeAnalysis;

namespace AiFramework.Infrastructure.Messaging;

/// <summary>
/// Collapses every registered <see cref="CommandDescriptor"/> into a lookup dictionary once,
/// at construction — registered as a singleton, so that happens at first resolution rather
/// than per request. <see cref="CommandDispatcher"/> is still scoped and holds a reference to
/// this. The duplicate-registration check moves here with it: it now fires once, at startup
/// (or at the first request if nothing resolves the registry eagerly), instead of on every
/// request.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<Type, CommandDescriptor> _descriptors;

    public CommandRegistry(IEnumerable<CommandDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        _descriptors = descriptors
            .GroupBy(d => d.CommandType)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single()
                : throw new InvalidOperationException(
                    $"Command '{g.Key.Name}' is registered {g.Count()} times. " +
                    "AddMessaging() must be called exactly once."));
    }

    public bool TryGet(Type commandType, [MaybeNullWhen(false)] out CommandDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(commandType);

        return _descriptors.TryGetValue(commandType, out descriptor);
    }
}

/// <summary>The query-side equivalent. See <see cref="CommandRegistry"/>.</summary>
public sealed class QueryRegistry
{
    private readonly Dictionary<Type, QueryDescriptor> _descriptors;

    public QueryRegistry(IEnumerable<QueryDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        _descriptors = descriptors
            .GroupBy(d => d.QueryType)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single()
                : throw new InvalidOperationException(
                    $"Query '{g.Key.Name}' is registered {g.Count()} times. " +
                    "AddMessaging() must be called exactly once."));
    }

    public bool TryGet(Type queryType, [MaybeNullWhen(false)] out QueryDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(queryType);

        return _descriptors.TryGetValue(queryType, out descriptor);
    }
}
