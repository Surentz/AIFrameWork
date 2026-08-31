using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Infrastructure.Messaging;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Infrastructure;

public static class InfrastructureRegistration
{
    /// <summary>
    /// Every command and query in the Application assembly must appear here. The
    /// registration-completeness test in Infrastructure.Tests fails the build if one is missed.
    /// </summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();

        services.AddCommand<PlaceOrder, Guid, PlaceOrderHandler>();
        services.AddQuery<GetOrder, OrderView, GetOrderHandler>();

        services.AddScoped<IValidator<PlaceOrder>, PlaceOrderValidator>();

        return services;
    }
}
