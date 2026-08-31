using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.Messaging;

public sealed class CommandDispatcher(
    IServiceProvider serviceProvider,
    IEnumerable<CommandDescriptor> descriptors) : ICommandDispatcher
{
    private readonly Dictionary<Type, CommandDescriptor> _descriptors =
        descriptors.ToDictionary(d => d.CommandType);

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

        var result = await descriptor.Invoke(serviceProvider, command, cancellationToken)
            .ConfigureAwait(false);

        return (Result<TResponse>)result!;
    }
}

public sealed class QueryDispatcher(
    IServiceProvider serviceProvider,
    IEnumerable<QueryDescriptor> descriptors) : IQueryDispatcher
{
    private readonly Dictionary<Type, QueryDescriptor> _descriptors =
        descriptors.ToDictionary(d => d.QueryType);

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

        var result = await descriptor.Invoke(serviceProvider, query, cancellationToken)
            .ConfigureAwait(false);

        return (Result<TResponse>)result!;
    }
}
