using AiFramework.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure.Messaging;

public static class MessagingRegistration
{
    /// <summary>
    /// Registers a command, its handler, and a dispatch delegate that closes over TCommand and
    /// TResponse at compile time. The static local function captures nothing, so there is no
    /// closure allocation — but TEvent-style generics still come from the enclosing method.
    /// </summary>
    public static IServiceCollection AddCommand<TCommand, TResponse, THandler>(
        this IServiceCollection services)
        where TCommand : ICommand<TResponse>
        where THandler : class, ICommandHandler<TCommand, TResponse>
    {
        ArgumentNullException.ThrowIfNull(services);

        static async Task<object?> InvokeAsync(
            IServiceProvider sp, object command, CancellationToken ct)
        {
            var handler = sp.GetRequiredService<ICommandHandler<TCommand, TResponse>>();
            return await handler.HandleAsync((TCommand)command, ct).ConfigureAwait(false);
        }

        services.AddScoped<ICommandHandler<TCommand, TResponse>, THandler>();
        return services.AddSingleton(new CommandDescriptor(typeof(TCommand), InvokeAsync));
    }

    public static IServiceCollection AddQuery<TQuery, TResponse, THandler>(
        this IServiceCollection services)
        where TQuery : IQuery<TResponse>
        where THandler : class, IQueryHandler<TQuery, TResponse>
    {
        ArgumentNullException.ThrowIfNull(services);

        static async Task<object?> InvokeAsync(
            IServiceProvider sp, object query, CancellationToken ct)
        {
            var handler = sp.GetRequiredService<IQueryHandler<TQuery, TResponse>>();
            return await handler.HandleAsync((TQuery)query, ct).ConfigureAwait(false);
        }

        services.AddScoped<IQueryHandler<TQuery, TResponse>, THandler>();
        return services.AddSingleton(new QueryDescriptor(typeof(TQuery), InvokeAsync));
    }
}
