using System.Threading.Channels;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Messaging;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure;

public static class InfrastructureRegistration
{
    /// <summary>
    /// Every command, query, and domain event in their respective assemblies must appear here.
    /// The registration-completeness tests in Infrastructure.Tests fail the build if one is
    /// missed. This is a composition FRAGMENT, not a self-sufficient root: several handlers
    /// registered here (PlaceOrderHandler, OrderPlacedAuditHandler) depend on services that are
    /// only registered by AddInfrastructure (IOrderRepository, IClock, IOrderAuditWriter).
    /// Calling AddMessaging() on its own and resolving one of them fails with a confusing DI
    /// error naming the missing dependency, not this method. Always reach this through
    /// AddInfrastructure, which calls it last.
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
        services.AddQuery<GetOrders, OrderPage, GetOrdersHandler>();

        services.AddScoped<IValidator<PlaceOrder>, PlaceOrderValidator>();

        services.AddDomainEvent<OrderPlaced>("order.placed");
        services.AddScoped<IDomainEventHandler<OrderPlaced>, OrderPlacedAuditHandler>();

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
        services.AddScoped<IOrderAuditWriter, OrderAuditWriter>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddOutbox();

        return services.AddMessaging();
    }

    /// <summary>The outbox pipeline. Called from AddInfrastructure; the hosted services start with the app.</summary>
    public static IServiceCollection AddOutbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Validated so a misconfigured value fails loudly instead of silently doing nothing.
        // WorkerCount = 0 in particular would otherwise make OutboxWorkerService.ExecuteAsync's
        // Task.WhenAll over an empty sequence complete immediately - no exception, no log, and
        // the outbox just stops delivering while the channel fills and backpressures the poller.
        services.AddOptions<OutboxOptions>()
            .Validate(o => o.WorkerCount >= 1, "OutboxOptions.WorkerCount must be at least 1.")
            .Validate(o => o.BatchSize >= 1, "OutboxOptions.BatchSize must be at least 1.")
            .Validate(o => o.ChannelCapacity >= 1, "OutboxOptions.ChannelCapacity must be at least 1.");

        // The channel is built from the CONFIGURED options, not from a fresh OutboxOptions() —
        // constructing one here would silently ignore any capacity the host configured. It is
        // registered as a singleton Channel<T>, with the reader and writer projected from it,
        // so both pumps provably share one instance (see OutboxRegistrationTests).
        services.AddSingleton(sp =>
        {
            var configured = sp.GetRequiredService<IOptions<OutboxOptions>>().Value;
            return Channel.CreateBounded<OutboxWorkItem>(
                new BoundedChannelOptions(configured.ChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = true,
                    SingleReader = false,
                });
        });

        services.AddSingleton(sp => sp.GetRequiredService<Channel<OutboxWorkItem>>().Writer);
        services.AddSingleton(sp => sp.GetRequiredService<Channel<OutboxWorkItem>>().Reader);
        services.AddScoped<OutboxPoller>();
        services.AddScoped<OutboxWorkItemProcessor>();
        services.AddHostedService<OutboxPollerService>();
        services.AddHostedService<OutboxWorkerService>();

        return services;
    }
}
