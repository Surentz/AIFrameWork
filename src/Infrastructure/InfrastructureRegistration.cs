using System.Threading.Channels;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Application.Products;
using AiFramework.Application.Users;
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.Caching;
using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Messaging;
using AiFramework.Infrastructure.Outbox;
using AiFramework.Infrastructure.Persistence;
using AiFramework.Infrastructure.Resilience;
using AiFramework.Infrastructure.Security;
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

        services.AddCommand<CreateProduct, Guid, CreateProductHandler>();
        services.AddCommand<UpdateProduct, bool, UpdateProductHandler>();
        services.AddQuery<GetProduct, ProductView, GetProductHandler>();
        services.AddQuery<GetProducts, ProductPage, GetProductsHandler>();

        services.AddCommand<RegisterUser, SessionView, RegisterUserHandler>();
        services.AddCommand<SignIn, SessionView, SignInHandler>();
        services.AddCommand<ChangePassword, SessionView, ChangePasswordHandler>();
        services.AddCommand<SignOutEverywhere, bool, SignOutEverywhereHandler>();
        services.AddQuery<GetUser, SessionView, GetUserHandler>();

        services.AddScoped<IValidator<PlaceOrder>, PlaceOrderValidator>();
        services.AddScoped<IValidator<CreateProduct>, CreateProductValidator>();
        services.AddScoped<IValidator<UpdateProduct>, UpdateProductValidator>();
        services.AddScoped<IValidator<RegisterUser>, RegisterUserValidator>();
        services.AddScoped<IValidator<SignIn>, SignInValidator>();
        services.AddScoped<IValidator<ChangePassword>, ChangePasswordValidator>();

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
            // Covers every repository read, every SaveChangesAsync the unit-of-work behavior
            // issues, and every ExecuteUpdateAsync - a transient fault (a Postgres pod
            // restarting under the kind cluster of ADR 0010) now retries instead of failing the
            // request. maxRetryDelay is 1s, not EF's 30s default, because AddDbContextCheck's
            // CanConnectAsync goes through this same strategy and backs /health/ready, whose
            // probe has no timeoutSeconds set - the Kubernetes default of 1s would otherwise let
            // Kubernetes time the probe out mid-retry instead of the strategy ever finishing it.
            // k8s/base/api.yaml sets an explicit timeoutSeconds to give the strategy room to
            // actually run. ADR 0014.
            //
            // One consequence worth knowing before it is discovered by surprise: with a
            // retrying execution strategy configured, EF throws if a caller opens an explicit
            // transaction (BeginTransactionAsync) without wrapping it in
            // Database.CreateExecutionStrategy().ExecuteAsync(...) - the strategy cannot retry a
            // block it does not own. Nothing in src/ does that today (the one BeginTransactionAsync
            // call is in a test, against its own context), so this costs nothing yet; see
            // Infrastructure/CLAUDE.md's EF rules for the standing rule it becomes.
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(1),
                errorCodesToAdd: null))
            .AddInterceptors(sp.GetRequiredService<DomainEventsInterceptor>()));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOrderAuditWriter, OrderAuditWriter>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ISessionValidator, SessionValidator>();
        services.AddSingleton<IClock, SystemClock>();
        // Singleton: PasswordHasher<T> is stateless and thread-safe, and the object it wraps
        // holds only the work-factor settings.
        services.AddSingleton<IPasswordHasher, PasswordHasher>();

        services.AddCaching();

        // Beside AddCaching, and inert until a typed client attaches a pipeline to it:
        // this registers and validates the retry budget, nothing more. ADR 0014.
        services.AddResilience();

        services.AddOutbox();

        // ADR 0005 spike: registers only what the Wolverine handler needs. The Wolverine host
        // itself is wired in Program.cs, because UseWolverine hooks IHostBuilder, not IServiceCollection.
        services.AddWolverineEventPathServices();

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
