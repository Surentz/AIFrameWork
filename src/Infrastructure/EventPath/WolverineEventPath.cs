using System.Collections.Concurrent;
using System.Reflection;
using AiFramework.Infrastructure.Jobs;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace AiFramework.Infrastructure.EventPath;

/// <summary>
/// What a host does with jobs. <b>This one value is the entire API/worker split</b> — same
/// assembly, same handlers, same AddInfrastructure; only whether <c>ListenToPostgresqlQueue</c>
/// is ever called differs. See ADR 0016.
/// </summary>
public enum WolverineHostRole
{
    /// <summary>
    /// Publishes jobs and listens on no job queue. The API. Routing rules are still registered —
    /// publishing is how a job starts — but no job handler is discovered here, so none can run in
    /// a process that is serving requests.
    /// </summary>
    PublishesJobs,

    /// <summary>
    /// Listens on the lanes named in <c>Jobs:Queues</c>, and runs the job handlers. The worker.
    /// </summary>
    ProcessesJobs,
}

/// <summary>
/// Wolverine's durable event path, per ADR 0005, running ALONGSIDE the hand-built outbox in
/// <c>Infrastructure/Outbox</c> rather than replacing it — nothing in the existing path calls into
/// this.
/// </summary>
/// <remarks>
/// No longer the removable spike ADR 0005 describes: ADR 0016 put the job framework on this same
/// runtime, so <see cref="WolverineHostRole"/> above and <c>JobRegistration</c> now depend on it.
/// Deleting this folder would take the worker with it.
/// </remarks>
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
    /// <param name="rabbitMqConnectionString">
    /// The broker's AMQP URI. Required whenever <paramref name="durable"/> is true - RabbitMQ is
    /// configured inside the durable branch, so <c>Wolverine__Durable=false</c> (codegen, the
    /// OpenAPI contract, HealthTests) turns it off with the Postgres transport and needs no switch
    /// of its own. ADR 0026.
    /// </param>
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
    /// <param name="role">
    /// Whether this host merely publishes jobs (the API) or also consumes them (the worker).
    /// See <see cref="WolverineHostRole"/> — this is the whole of the split.
    /// </param>
    /// <param name="jobOptions">
    /// Which lanes to listen on and at what parallelism. Passed in rather than resolved, the same
    /// shape CacheOptions and ResilienceOptions already use: each host reads its own configuration
    /// and hands the values to Infrastructure. Ignored entirely when
    /// <paramref name="role"/> is <see cref="WolverineHostRole.PublishesJobs"/>.
    /// </param>
    public static IHostBuilder AddWolverineEventPath(
        this IHostBuilder host,
        string connectionString,
        string? rabbitMqConnectionString,
        Assembly applicationAssembly,
        WolverineHostRole role,
        JobOptions? jobOptions = null,
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
            // Handler DISCOVERY, unconditionally — it has nothing to do with the transport, and
            // separating it from the transport wiring below is load-bearing for two reasons:
            //
            //  - `codegen write` runs with Wolverine__Durable=false, because generating code must
            //    not need a database. Discovery inside the durable branch meant the worker's
            //    generated tree came out with NO job adapters at all: it wrote only
            //    OrderPlacedNotificationHandler and Release would then fail at startup with
            //    MissingPreBuiltTypesException on the first job. Found by reading what the
            //    command actually wrote, not by reasoning.
            //  - A MediatorOnly host with a discovered handler is harmless; one with a
            //    database-backed ROUTE is not (see below).
            if (role is WolverineHostRole.ProcessesJobs)
            {
                JobRegistration.IncludeJobHandlers(opts);
            }

            ConfigureTransport(opts, connectionString, rabbitMqConnectionString, role, jobOptions, durable);

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
    /// Durability, and the transport wiring that depends on it. Extracted so the UseWolverine
    /// lambda stays under Meziantou's MA0051 length limit — the rule is satisfied, not suppressed.
    /// </summary>
    private static void ConfigureTransport(
        WolverineOptions opts,
        string connectionString,
        string? rabbitMqConnectionString,
        WolverineHostRole role,
        JobOptions? jobOptions,
        bool durable)
    {
        if (!durable)
        {
            opts.Durability.Mode = DurabilityMode.MediatorOnly;
            return;
        }

        ConfigureDurability(opts, connectionString);

        // Before ConfigureJobs, for the same ordering reason given below: a route to a broker
        // queue needs the transport it names to be registered already.
        ConfigureRabbitMq(opts, rabbitMqConnectionString, role);

        // AFTER ConfigureDurability, and only when durable. Both halves matter, and both were
        // found by failing tests rather than reasoned out:
        //
        //  - Order: job routing is expressed as ToPostgresqlQueue, which needs the Postgres
        //    transport that PersistMessagesWithPostgresql registers. Configured first, it has
        //    nothing to attach to.
        //  - Condition: MediatorOnly has no transport at all, by definition — that mode exists
        //    precisely so a host can start with no reachable database. Registering a
        //    database-backed route there reintroduces the startup connection the mode is for
        //    avoiding, which is what took HealthTests, OpenApiDocumentTests, ForwardedHeadersTests
        //    and AuthRateLimitTests to ~19s timeouts in one step. A MediatorOnly host cannot
        //    publish a job anyway; nothing is lost.
        ConfigureJobs(opts, role, jobOptions);
    }

    /// <summary>
    /// The job half of the configuration, and the one place the API and the worker differ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Routing is registered on every host; listening is not.</b> The API needs
    /// <c>MapJobs</c> so that <c>IJobScheduler.EnqueueAsync</c> knows which queue a job belongs
    /// on, and it must never reach <c>ListenForJobs</c> or <c>IncludeJobHandlers</c> — those two
    /// calls are what would put job work on a process that is serving requests.
    /// </para>
    /// <para>
    /// <c>ApiPublishesOnlyTests</c> asserts the API side of this against the runtime's own
    /// endpoint list, so the rule is enforced rather than merely intended.
    /// </para>
    /// </remarks>
    private static void ConfigureJobs(
        WolverineOptions opts, WolverineHostRole role, JobOptions? jobOptions)
    {
        JobRegistration.MapJobs(opts);

        if (role is not WolverineHostRole.ProcessesJobs)
        {
            return;
        }

        // Discovery already happened above, outside the durable branch — see the comment there.
        // Error policy lands here because MoveToErrorQueue needs the envelope storage that only
        // the durable branch registers.
        JobRegistration.ConfigureJobErrorHandling(opts);

        // Null here is a wiring mistake, not a default to paper over: a worker with no JobOptions
        // would start, listen to nothing, and look perfectly healthy while its queues filled.
        JobRegistration.ListenForJobs(
            opts,
            jobOptions ?? throw new ArgumentNullException(
                nameof(jobOptions),
                $"A host in the {nameof(WolverineHostRole.ProcessesJobs)} role must be given JobOptions."));
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
        //
        // That registration now also configures EnableRetryOnFailure (ADR 0014), and the two
        // interact: SaveChangesAndFlushMessagesAsync opens its own transaction internally, which
        // a retrying execution strategy refuses to run un-wrapped
        // ("does not support user-initiated transactions"). WolverineOutboxAtomicityTests hit
        // this directly and now wraps its call in
        // context.Database.CreateExecutionStrategy().ExecuteAsync(...) — any future handler that
        // adopts IDbContextOutbox<T> for real needs the same wrapping, not just this test.
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

    /// <summary>The broker, per ADR 0026. Only ever reached when durable.</summary>
    private static void ConfigureRabbitMq(
        WolverineOptions opts, string? rabbitMqConnectionString, WolverineHostRole role)
    {
        // Loud, like Program.cs's ConnectionStrings:Default guard. A durable host with no broker
        // configured is a misconfiguration, and starting "healthy" with no transport would queue
        // every job into nowhere.
        if (string.IsNullOrWhiteSpace(rabbitMqConnectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:RabbitMq is required when Wolverine is durable. Set " +
                "ConnectionStrings__RabbitMq (double underscores), or Wolverine__Durable=false for a " +
                "host that must start without infrastructure.");
        }

        var rabbit = opts.UseRabbitMq(new Uri(rabbitMqConnectionString))
            // Declares what the routes and listeners name, at startup. Needs the broker reachable,
            // which is the fail-fast contract: an unreachable broker stops the host (spec section 6).
            .AutoProvision()
            // Failures go to Wolverine's Postgres dead-letter storage, which the monitoring page
            // lists and retries - never to a RabbitMQ-native DLQ nobody would look at.
            .DisableDeadLetterQueueing()
            .UseQuorumQueues()
            // On both hosts. Left on, EVERY host - even a sender-only one - declares a classic,
            // auto-delete wolverine.response.<guid> queue and listens on it for request/reply,
            // which nothing here uses: the API would consume from the broker after all (ADR 0016),
            // and the queue would break "quorum everywhere". TopologyTests caught the listener.
            .DisableSystemRequestReplyQueueDeclaration()
            .ConfigureChannelCreation(channel =>
            {
                channel.PublisherConfirmationsEnabled = true;
                channel.PublisherConfirmationTrackingEnabled = true;
            });

        // The API only sends. A sender-only connection is the transport-level half of
        // ApiPublishesOnlyTests: there is no listening connection to attach a listener to. Not
        // sufficient on its own - see the reply queue above - and TopologyTests pins the result.
        if (role is WolverineHostRole.PublishesJobs)
        {
            rabbit.UseSenderConnectionOnly();
        }

        RabbitMqTopology.Declare(rabbit);

        // Every sending endpoint durable: a job or an event is written to Postgres before the broker
        // sees it, and a broker outage only delays it.
        opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
        opts.Policies.UseDurableInboxOnAllListeners();
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
