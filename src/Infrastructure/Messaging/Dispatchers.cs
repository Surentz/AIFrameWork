using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.Messaging;

public sealed class CommandDispatcher(
    IServiceProvider serviceProvider,
    IEnumerable<CommandDescriptor> descriptors) : ICommandDispatcher
{
    private readonly Dictionary<Type, CommandDescriptor> _descriptors = descriptors
        .GroupBy(d => d.CommandType)
        .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single()
            : throw new InvalidOperationException(
                $"Command '{g.Key.Name}' is registered {g.Count()} times. " +
                "AddMessaging() must be called exactly once."));

    public async Task<Result<TResponse>> SendAsync<TResponse>(
        ICommand<TResponse> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_descriptors.TryGetValue(command.GetType(), out var descriptor))
        {
            throw new InvalidOperationException(
                $"No handler registered for command '{command.GetType().Name}'. " +
                "Add services.AddCommand<...>() in the composition root.");
        }

        var result = await descriptor.Dispatch(serviceProvider, command, cancellationToken)
            .ConfigureAwait(false);

        // Null-forgiving is safe here: the descriptor's Dispatch always resolves to
        // ICommandHandler<TCommand, TResponse>.HandleAsync, which always returns a non-null
        // Result<TResponse> boxed as object? — result is never actually null at runtime.
        return (Result<TResponse>)result!;
    }
}

public sealed class QueryDispatcher(
    IServiceProvider serviceProvider,
    IEnumerable<QueryDescriptor> descriptors) : IQueryDispatcher
{
    private readonly Dictionary<Type, QueryDescriptor> _descriptors = descriptors
        .GroupBy(d => d.QueryType)
        .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single()
            : throw new InvalidOperationException(
                $"Query '{g.Key.Name}' is registered {g.Count()} times. " +
                "AddMessaging() must be called exactly once."));

    public async Task<Result<TResponse>> SendAsync<TResponse>(
        IQuery<TResponse> query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!_descriptors.TryGetValue(query.GetType(), out var descriptor))
        {
            throw new InvalidOperationException(
                $"No handler registered for query '{query.GetType().Name}'. " +
                "Add services.AddQuery<...>() in the composition root.");
        }

        var result = await descriptor.Dispatch(serviceProvider, query, cancellationToken)
            .ConfigureAwait(false);

        // Null-forgiving is safe here: the descriptor's Dispatch always resolves to
        // IQueryHandler<TQuery, TResponse>.HandleAsync, which always returns a non-null
        // Result<TResponse> boxed as object? — result is never actually null at runtime.
        return (Result<TResponse>)result!;
    }
}
