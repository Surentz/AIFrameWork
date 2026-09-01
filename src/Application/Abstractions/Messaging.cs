namespace AiFramework.Application.Abstractions;

/// <summary>
/// A request that changes state. A type may implement this EXACTLY ONCE — the dispatcher
/// infers TResponse from the argument, and two implementations make that ambiguous.
/// </summary>
#pragma warning disable S2326 // TResponse is a phantom type parameter for call-site inference; load-bearing despite not appearing in method signatures
public interface ICommand<TResponse>;
#pragma warning restore S2326

/// <summary>A request that reads state. Same single-implementation rule as ICommand.</summary>
#pragma warning disable S2326 // TResponse is a phantom type parameter for call-site inference; load-bearing despite not appearing in method signatures
public interface IQuery<TResponse>;
#pragma warning restore S2326

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    public Task<Result<TResponse>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

public interface ICommandDispatcher
{
    public Task<Result<TResponse>> SendAsync<TResponse>(
        ICommand<TResponse> command, CancellationToken cancellationToken);
}

public interface IQueryDispatcher
{
    public Task<Result<TResponse>> SendAsync<TResponse>(
        IQuery<TResponse> query, CancellationToken cancellationToken);
}
