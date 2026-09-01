using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Messaging;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
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

        // CommandRegistry/QueryRegistry are singletons: the dictionary they build from the
        // descriptors below (including the duplicate-registration check) is built once, at
        // first resolution, not per request. The dispatchers that hold them stay scoped.
        services.AddSingleton<CommandRegistry>();
        services.AddSingleton<QueryRegistry>();
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();

        services.AddCommand<PlaceOrder, Guid, PlaceOrderHandler>();
        services.AddQuery<GetOrder, OrderView, GetOrderHandler>();

        services.AddScoped<IValidator<PlaceOrder>, PlaceOrderValidator>();

        services.AddDomainEvent<OrderPlaced>("order.placed");

        return services;
    }

    /// <summary>The single entry point Api calls. Api must not reach past this into Infrastructure.</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<DomainEventRegistry>();
        services.AddSingleton<DomainEventsInterceptor>();
        services.AddDbContext<AiFrameworkDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .AddInterceptors(sp.GetRequiredService<DomainEventsInterceptor>()));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddSingleton<IClock, SystemClock>();

        return services.AddMessaging();
    }
}
