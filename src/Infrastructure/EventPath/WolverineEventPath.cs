using System.Collections.Concurrent;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Postgresql;

namespace AiFramework.Infrastructure.EventPath;

/// <summary>
/// A spike, per ADR 0005: Wolverine's durable event path running ALONGSIDE the hand-built
/// outbox in <c>Infrastructure/Outbox</c>, not replacing it. Nothing in the existing path
/// calls into this, and removing this folder plus the one line in Program.cs reverts it.
/// </summary>
public static class WolverineEventPath
{
    /// <summary>
    /// Wolverine's envelope tables live in their own schema rather than beside the EF Core
    /// tables. Wolverine provisions them itself, outside `dotnet ef migrations` — keeping them
    /// in a separate schema is what stops two schema authorities from arguing over `public`.
    /// </summary>
    public const string EnvelopeSchema = "wolverine";

    /// <summary>
    /// The single entry point Api calls, mirroring AddInfrastructure. It is an IHostBuilder
    /// extension rather than an IServiceCollection one because UseWolverine hooks the host.
    /// </summary>
    /// <param name="host">The host builder to attach Wolverine to.</param>
    /// <param name="connectionString">The same PostgreSQL connection string the DbContext uses.</param>
    /// <param name="durable">
    /// When false, Wolverine runs in <c>DurabilityMode.MediatorOnly</c>: no envelope storage, no
    /// inbox, no outbox, and — the reason this switch exists — no database connection at startup.
    ///
    /// Durable Wolverine migrates its envelope schema while the host starts, which means the
    /// application no longer boots at all against an unreachable database. That is a real change
    /// in this app's startup contract, not a test detail: today <c>/health</c> answers without
    /// ever touching Postgres, and `HealthTests` relies on exactly that to avoid paying for a
    /// container. Discovered by that test failing with "Failed to connect to 127.0.0.1:5432"
    /// the moment Wolverine was wired in. Recorded in ADR 0005 as a consequence.
    /// </param>
    public static IHostBuilder AddWolverineEventPath(
        this IHostBuilder host, string connectionString, bool durable = true)
    {
        ArgumentNullException.ThrowIfNull(host);

        return host.UseWolverine(opts =>
        {
            if (durable)
            {
                // CreateOrUpdate, not All: All drops and rebuilds, which would be catastrophic
                // pointed at a real database. Wolverine patches its own schema on startup — the
                // envelope tables are outside `dotnet ef migrations` by design (ADR 0005).
                opts.PersistMessagesWithPostgresql(connectionString, EnvelopeSchema)
                    .OverrideAutoCreateResources(AutoCreate.CreateOrUpdate);
            }
            else
            {
                opts.Durability.Mode = DurabilityMode.MediatorOnly;
            }

            // Load-bearing. Wolverine's conventional discovery claims any type whose name ends
            // in "Handler" or "Consumer" with a Handle/Consume method — which is every handler
            // this repo already has: PlaceOrderHandler, GetOrderHandler, GetOrdersHandler,
            // OrderPlacedAuditHandler. Left on, Wolverine would register PlaceOrder as one of
            // its own message types and stand a second dispatch path up beside the one ADR 0003
            // built. Discovery is off, and each Wolverine handler is named explicitly.
            opts.Discovery
                .DisableConventionalDiscovery()
                .IncludeType<OrderPlacedNotificationHandler>();
        });
    }

    /// <summary>Registers what the spike's handler needs. Called from AddInfrastructure.</summary>
    public static IServiceCollection AddWolverineEventPathServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<OrderPlacedNotificationRecorder>();

        return services;
    }
}

/// <summary>The spike's message. Deliberately not <c>OrderPlaced</c>: the domain event stays
/// owned by the existing outbox, so the two paths cannot interfere while both are running.</summary>
public sealed record OrderPlacedNotification(Guid OrderId, string Sku, int Quantity);

/// <summary>
/// Proof that a message published through Wolverine reached a handler. A singleton bag rather
/// than a database table — this exists to make the spike assertable and goes away with it.
/// </summary>
public sealed class OrderPlacedNotificationRecorder
{
    private readonly ConcurrentDictionary<Guid, int> _handled = new();

    public void Record(Guid orderId) => _handled.AddOrUpdate(orderId, 1, static (_, count) => count + 1);

    public bool WasHandled(Guid orderId) => _handled.ContainsKey(orderId);

    public int TimesHandled(Guid orderId) => _handled.TryGetValue(orderId, out var count) ? count : 0;
}

/// <summary>
/// Wolverine finds this by the explicit IncludeType above, not by the name.
/// </summary>
/// <remarks>
/// The recorder arrives by constructor injection rather than as a Handle parameter, which
/// Wolverine also supports. That is not a style preference: with the recorder as a method
/// argument, Handle touches no instance state, and CA1822/S2325 demand it be static — while
/// S1118 then demands the whole class be static, which <c>IncludeType&lt;T&gt;</c> cannot
/// accept as a type argument. Constructor injection is the shape that satisfies all three.
/// </remarks>
public sealed class OrderPlacedNotificationHandler(OrderPlacedNotificationRecorder recorder)
{
    public void Handle(OrderPlacedNotification message)
    {
        ArgumentNullException.ThrowIfNull(message);

        recorder.Record(message.OrderId);
    }
}
