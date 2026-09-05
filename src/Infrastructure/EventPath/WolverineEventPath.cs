using System.Collections.Concurrent;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;
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
    /// <param name="applicationAssembly">
    /// The assembly Wolverine treats as "the application", and therefore the one it loads
    /// pre-generated handler adapters from in Release. Passed in rather than inferred: left to
    /// itself Wolverine picks the assembly that called UseWolverine — Infrastructure — while
    /// `codegen write` writes into the Api project, so Release failed with
    /// MissingPreBuiltTypesException until the two were pointed at the same assembly. It is a
    /// parameter rather than typeof(Program).Assembly here because Infrastructure cannot
    /// reference Api, and rather than Assembly.GetEntryAssembly() because under `dotnet test`
    /// that resolves to the test host instead of the Api.
    /// </param>
    /// <param name="usePreGeneratedCode">
    /// Whether Wolverine must load handler adapters that were generated ahead of time
    /// (<c>TypeLoadMode.Static</c>) instead of compiling them at startup with Roslyn.
    ///
    /// Defaults to false in Debug and true in Release, mirroring the Debug-only
    /// WolverineFx.RuntimeCompilation package reference in AiFramework.Infrastructure.csproj —
    /// Release has no compiler to fall back on. It is a parameter rather than a bare
    /// <c>#if</c> so that a Debug test can still opt in and prove the committed code under
    /// src/Api/Internal/Generated is current; without that, stale generated code stays green
    /// in Debug and only breaks in Release. See WolverineCodegenTests.
    /// </param>
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
        this IHostBuilder host,
        string connectionString,
        Assembly applicationAssembly,
        bool durable = true,
        bool usePreGeneratedCode =
#if DEBUG
            false
#else
            true
#endif
        )
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(applicationAssembly);

        return host.UseWolverine(opts =>
        {
            if (durable)
            {
                ConfigureDurability(opts, connectionString);
            }
            else
            {
                opts.Durability.Mode = DurabilityMode.MediatorOnly;
            }

            // Static rather than Auto deliberately: Auto silently falls back to generating code
            // at runtime, which in Release means failing later and less clearly. Static throws
            // at startup when a pre-built type is missing, so forgetting to re-run codegen after
            // adding or changing a handler is caught immediately rather than in production.
            if (usePreGeneratedCode)
            {
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
            }

            // Set in every configuration, not just Release: it is what Static mode reads, and
            // leaving Debug on a different assembly would mean codegen write and the Release
            // load path could silently disagree.
            opts.ApplicationAssembly = applicationAssembly;

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

    /// <summary>
    /// The durable half of the configuration. Extracted so the UseWolverine lambda stays under
    /// Meziantou's MA0051 length limit — the rule is satisfied rather than suppressed.
    /// </summary>
    private static void ConfigureDurability(WolverineOptions opts, string connectionString)
    {
        // CreateOrUpdate, not All: All drops and rebuilds, which would be catastrophic
        // pointed at a real database. Wolverine patches its own schema on startup — the
        // envelope tables are outside `dotnet ef migrations` by design (ADR 0005).
        opts.PersistMessagesWithPostgresql(connectionString, EnvelopeSchema)
            .OverrideAutoCreateResources(AutoCreate.CreateOrUpdate);

        // What makes IDbContextOutbox<AiFrameworkDbContext> available: messages published
        // through it are held until the DbContext's transaction commits, and discarded if
        // it does not. Deliberately NOT AddDbContextWithWolverineIntegration, which would
        // replace this repo's own AddDbContext registration (and force its options to a
        // singleton lifetime). This way the existing registration in
        // InfrastructureRegistration — interceptor and all — is untouched.
        opts.UseEntityFrameworkCoreTransactions();

        // Local queues are BufferedInMemory unless enrolled, which means a message sitting in
        // one when the process dies is gone. That is not an exotic failure on either deployment
        // target: IIS recycles app pools on a schedule and shuts the worker down after an idle
        // timeout, and Kubernetes reschedules pods for deploys, drains and scaling. Both kill
        // BackgroundServices, and this app's message handling is one.
        //
        // Measured before this line existed, via ServiceCapabilities.MessagingEndpoints:
        //   local://...orderplacednotification/  mode=BufferedInMemory
        // and Durable after. WolverineLocalQueueDurabilityTests pins it.
        opts.Policies.UseDurableLocalQueues();
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
