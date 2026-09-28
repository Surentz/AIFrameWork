using AiFramework.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Messaging;

public static class MessagingRegistration
{
    /// <summary>
    /// Registers a command, its handler, and a dispatch delegate that closes over TCommand and
    /// TResponse at compile time. The local function is <c>static</c> so it captures nothing —
    /// no closure allocation — while TCommand and TResponse still come from the enclosing
    /// generic method's type parameters, letting <see cref="CommandDispatcher"/> invoke the
    /// right handler with no reflection.
    /// <see cref="Behaviors.LoggedAsync{TRequest,TResponse}"/> is the outermost wrapper, so a
    /// logged duration covers validation, the handler, commit and eviction together — the inner
    /// lambda it wraps is itself <c>static</c>, for the same no-closure reason as this method.
    /// </summary>
    public static IServiceCollection AddCommand<TCommand, TResponse, THandler>(
        this IServiceCollection services)
        where TCommand : ICommand<TResponse>
        where THandler : class, ICommandHandler<TCommand, TResponse>
    {
        ArgumentNullException.ThrowIfNull(services);

        static async Task<object?> InvokeAsync(
            IServiceProvider sp, object command, CancellationToken ct) =>
            await Behaviors.LoggedAsync<TCommand, TResponse>(
                sp, "Command", (TCommand)command, static async (sp, typed, ct) =>
                {
                    var failed = await Behaviors.ValidateAsync<TCommand, TResponse>(sp, typed, ct)
                        .ConfigureAwait(false);
                    if (failed is not null)
                    {
                        return failed;
                    }

                    var handler = sp.GetRequiredService<ICommandHandler<TCommand, TResponse>>();
                    var result = await handler.HandleAsync(typed, ct).ConfigureAwait(false);

                    // Reassigned: a commit that loses a concurrency race turns a successful
                    // result into a Conflict, and eviction must then see the failure and skip.
                    result = await Behaviors.CommitAsync(sp, result, ct).ConfigureAwait(false);
                    await Behaviors.EvictAsync(sp, typed, result, ct).ConfigureAwait(false);

                    return result;
                }, ct).ConfigureAwait(false);

        services.AddScoped<ICommandHandler<TCommand, TResponse>, THandler>();
        return services.AddSingleton(new CommandDescriptor(typeof(TCommand), InvokeAsync));
    }

    /// <summary>
    /// Registers a query, its handler, and a dispatch delegate that closes over TQuery and
    /// TResponse at compile time. Same reflection-free mechanism as <see cref="AddCommand"/>,
    /// via <see cref="QueryDispatcher"/>. Unlike the command path this runs no validation —
    /// query handlers validate their own inputs — but it does run the caching behavior, which
    /// is a no-op for a query that has not opted in with ICacheable.
    /// <see cref="Behaviors.LoggedAsync{TRequest,TResponse}"/> wraps the cache, not the other
    /// way round, so a logged duration reflects what the caller actually waited for — a cache
    /// hit included.
    /// </summary>
    public static IServiceCollection AddQuery<TQuery, TResponse, THandler>(
        this IServiceCollection services)
        where TQuery : IQuery<TResponse>
        where THandler : class, IQueryHandler<TQuery, TResponse>
    {
        ArgumentNullException.ThrowIfNull(services);

        static async Task<object?> InvokeAsync(
            IServiceProvider sp, object query, CancellationToken ct) =>
            await Behaviors.LoggedAsync<TQuery, TResponse>(
                sp, "Query", (TQuery)query,
                static (sp, typed, ct) => Behaviors.CachedAsync<TQuery, TResponse>(sp, typed, ct),
                ct).ConfigureAwait(false);

        services.AddScoped<IQueryHandler<TQuery, TResponse>, THandler>();
        return services.AddSingleton(new QueryDescriptor(typeof(TQuery), InvokeAsync));
    }
}
