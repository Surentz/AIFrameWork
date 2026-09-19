using AiFramework.Application.Abstractions;
using AiFramework.Application.Maintenance;
using AiFramework.Application.Orders;
using AiFramework.Infrastructure.Outbox;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;

namespace AiFramework.Infrastructure.Jobs;

/// <summary>
/// One job's registration: its type, its lane, how it is routed, and — for a scheduled job — its
/// default cron and how to create one. The job-side equivalent of
/// <c>CommandDescriptor</c>/<c>QueryDescriptor</c>.
/// </summary>
/// <remarks>
/// <see cref="Route"/> and <see cref="EnqueueNew"/> are delegates captured over the closed generic
/// at construction time, so routing and scheduled enqueueing stay <b>reflection-free</b> — the same
/// posture <c>AddCommand</c>/<c>AddQuery</c> hold. <see cref="JobType"/> exists so completeness
/// tests can read the list without executing it.
/// </remarks>
public sealed record JobDescriptor(
    Type JobType,
    JobLane Lane,
    Action<WolverineOptions> Route,
    string? DefaultCron = null,
    Func<IJobScheduler, CancellationToken, Task>? EnqueueNew = null)
{
    /// <summary>The job's key everywhere a string is needed: Quartz keys and config overrides.</summary>
    public string Name => JobType.Name;

    public bool IsScheduled => DefaultCron is not null;

    public static JobDescriptor For<TJob>()
        where TJob : IJob =>
        new(typeof(TJob), TJob.Lane, RouteFor<TJob>());

    /// <summary>
    /// A job that also runs on a schedule. <c>new()</c> is the point: a schedule fires with no caller
    /// and no arguments, so a job that needs data (an owner, an id) cannot be scheduled, and the
    /// compiler says so rather than a worker firing it with defaults. ADR 0017.
    /// </summary>
    /// <param name="cron">A Quartz cron expression — seconds first, e.g. <c>"0 5 * * * ?"</c>.</param>
    public static JobDescriptor Scheduled<TJob>(string cron)
        where TJob : IJob, new() =>
        new(
            typeof(TJob),
            TJob.Lane,
            RouteFor<TJob>(),
            cron,
            static (jobs, cancellationToken) => jobs.EnqueueAsync(new TJob(), cancellationToken));

    private static Action<WolverineOptions> RouteFor<TJob>()
        where TJob : IJob =>
        static opts => opts.PublishMessage<TJob>()
            .ToPostgresqlQueue(JobRegistration.QueueFor(TJob.Lane));
}

/// <summary>
/// The job framework's wiring: lane-to-queue mapping, the explicit job list, the listeners a host
/// opts into, and the retry policy. See ADR 0016.
/// </summary>
public static class JobRegistration
{
    /// <summary>
    /// <b>Every <see cref="IJob"/> in the Application assembly must appear here.</b> Explicit and
    /// greppable, mirroring <c>AddMessaging()</c>, and <c>JobRegistrationTests</c> fails the build
    /// when one is missing — the only thing standing in for compile-time safety, exactly as
    /// <c>RegistrationCompletenessTests</c> is for commands and queries.
    /// </summary>
    /// <remarks>
    /// A list rather than a sequence of calls inside <see cref="MapJobs"/>, specifically so the
    /// completeness test can compare it against the assembly scan without standing up a Wolverine
    /// host. One list, one source of truth: what is registered and what the test checks cannot
    /// drift apart.
    /// </remarks>
    public static IReadOnlyList<JobDescriptor> Jobs { get; } =
    [
        JobDescriptor.For<SendOrderConfirmation>(),
        JobDescriptor.For<RebuildOrderReport>(),

        // Hourly at :05. Retention is seven days, so hourly is already generous; the old
        // five-minute cadence existed only because the sweep piggy-backed on the poll loop.
        JobDescriptor.Scheduled<PruneProcessedOutbox>("0 5 * * * ?"),
    ];

    /// <summary>
    /// The one place a lane becomes a queue name. Both the publish side (<see cref="MapJobs"/>)
    /// and the listen side (<see cref="ListenForJobs"/>) go through it, so the API cannot publish
    /// to a name the worker is not listening on — the same single-source rule
    /// <c>CacheScope</c> holds for keys and tags, and for the same reason: two copies drift
    /// silently, with no exception and no failing test.
    /// </summary>
    /// <remarks>
    /// <b>Underscores, not hyphens.</b> The Postgres transport sanitises a queue name into an
    /// identifier, so "jobs-light" becomes the endpoint <c>postgresql://jobs_light/</c> — measured,
    /// not assumed: <c>ApiPublishesOnlyTests</c> was written against hyphens and failed with the
    /// real names in its message. Naming them the way they actually exist keeps what is written
    /// here matching what shows up in the database, in the endpoint list, and in a log line.
    /// </remarks>
    public static string QueueFor(JobLane lane) => lane switch
    {
        JobLane.Light => "jobs_light",
        JobLane.Heavy => "jobs_heavy",
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown job lane."),
    };

    /// <summary>
    /// Attaches every job's routing rule.
    /// </summary>
    /// <remarks>
    /// Runs on EVERY host, including the API. Routing rules are how a job gets published; they
    /// say nothing about who handles it. The listen side below is the host split.
    /// </remarks>
    public static void MapJobs(WolverineOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        foreach (var job in Jobs)
        {
            job.Route(opts);
        }
    }

    /// <summary>
    /// The handler types the worker runs. Named explicitly because conventional discovery is off
    /// repo-wide (see <c>WolverineEventPath</c>), and <b>this list is the enforcement point for
    /// "jobs never run in the API"</b> — the API never calls this.
    /// </summary>
    public static void IncludeJobHandlers(WolverineOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        opts.Discovery
            .IncludeType<SendOrderConfirmationHandler>()
            .IncludeType<RebuildOrderReportHandler>()
            .IncludeType<PruneProcessedOutboxHandler>();

        // Set on the WORKER only — the API keeps Wolverine 6's NotAllowed default, so this
        // relaxation reaches exactly the host that needs it.
        //
        // A job handler that reuses an application use case injects ICommandDispatcher or
        // IQueryDispatcher, and this repo's dispatchers take IServiceProvider by construction:
        // ADR 0003's reflection-free dispatch resolves the handler from the container at dispatch
        // time. Wolverine reads that as service location and, under NotAllowed, refuses to
        // generate the adapter at all — "Found service locations while generating code for
        // Message Handler for RebuildOrderReport". It fails only in `codegen write`; the code
        // compiles perfectly well, which is exactly the class of trap ADR 0005 already records.
        //
        // The alternative was to have job handlers inject repositories directly and bypass the
        // dispatchers. That was rejected: going through the use case is what keeps validation and
        // Behaviors.LoggedAsync on the path, so a job's reads are recorded the same way a
        // request's are. AlwaysAllowed rather than AllowedButWarn because the warning would fire
        // on every job chain at every startup, for a design decision that is deliberate.
        opts.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;

        // Reaches every user-scoped job automatically, so a new one cannot forget to populate its
        // caller. Wolverine generates the Before call into each handler's adapter, which is why
        // touching JobUserMiddleware means re-running the worker's `codegen write`.
        //
        // The predicate overload, not ForMessagesOfType<IUserScopedJob>().AddMiddleware<T>():
        // neither type argument is legal there. IUserScopedJob inherits IJob's static abstract
        // Lane and so has no most specific implementation (CS8920), and JobUserMiddleware is a
        // static class (CS0718). Both constraints are real rather than stylistic — making the
        // middleware non-static to satisfy one would then trip S1118/CA1822 on a class with no
        // instance state, the same bind WolverineEventPath's OrderPlacedNotificationHandler
        // records. Matching on the chain's message type sidesteps both.
        opts.Policies.AddMiddleware(
            typeof(JobUserMiddleware),
            chain => chain.MessageType.IsAssignableTo(typeof(IUserScopedJob)));
    }

    /// <summary>
    /// Attaches a listener per lane this host consumes. A host whose <c>Jobs:Queues</c> is empty
    /// registers none and is publish-only — which is exactly what the API does.
    /// </summary>
    public static void ListenForJobs(WolverineOptions opts, JobOptions options)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(options);

        foreach (var lane in options.ParseQueues())
        {
            opts.ListenToPostgresqlQueue(QueueFor(lane))
                .MaximumParallelMessages(options.ParallelismFor(lane));
        }
    }

    /// <summary>
    /// Retry and dead-lettering, as policy. Nothing in <c>Jobs/</c> reimplements the
    /// <c>NextAttemptAt</c> backoff arithmetic <c>Outbox/</c> already carries — not writing it a
    /// second time is a large part of why jobs ride Wolverine at all.
    /// </summary>
    /// <remarks>
    /// <b>ScheduleRetry, not RetryWithCooldown.</b> A cooldown holds the message — and therefore a
    /// listener slot — in memory for the whole delay. On the heavy lane, which runs at a
    /// parallelism of 2, one poisoned message sitting through a 30-minute cooldown would consume
    /// half the pod's capacity. ScheduleRetry puts the envelope back into durable storage and
    /// frees the slot immediately.
    /// </remarks>
    public static void ConfigureJobErrorHandling(WolverineOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        opts.OnAnyException()
            .ScheduleRetry(
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(30))
            .Then.MoveToErrorQueue();

        // Matches Behaviors.LoggedAsync's "success is Debug" convention (root CLAUDE.md's Logging
        // section). Wolverine logs a line per message at Information by default, which for a light
        // lane running thousands a day is the same "it worked" noise that convention exists to
        // keep out of the log store.
        opts.Policies.MessageSuccessLogLevel(LogLevel.Debug);
    }

    /// <summary>Registers what the job framework needs. Called from AddInfrastructure.</summary>
    public static IServiceCollection AddJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<JobOptions>()
            .Validate(
                o => o.LightParallelism >= 1 && o.HeavyParallelism >= 1,
                "JobOptions parallelism must be at least 1 for every lane.")
            // Without this the validators here are dead code in both hosts: nothing resolves
            // IOptions<JobOptions> at runtime, because each host binds the options straight off
            // IConfiguration at configuration time (UseWolverine hooks the host builder, before
            // any provider exists). ValidateOnStart forces that resolution at startup.
            //
            // JobOptions.Validate() is the same checks in a form a host can call directly on the
            // raw-bound instance, which is what actually guards the configuration-time path.
            .ValidateOnStart();

        // A validator rather than another .Validate(predicate, message) call, because the useful
        // message names the offending value and Validate's message is a constant. An unknown lane
        // must fail at startup rather than leave a queue with no consumer and nothing to say so —
        // the same intent as OutboxOptions' WorkerCount >= 1 validation.
        services.AddSingleton<IValidateOptions<JobOptions>, JobOptionsValidator>();

        services.AddScoped<IJobScheduler, JobScheduler>();

        // Scoped, and Wolverine creates a scope per message, so one instance serves one job.
        //
        // NOT registered as ICurrentUser here, and that is load-bearing. AddJobs runs inside
        // AddInfrastructure, which every host calls — so binding ICurrentUser to this would
        // silently REPLACE the API's cookie-backed CurrentUser, because Program.cs registers that
        // first and the last registration wins. Every authenticated request then reports no
        // caller. Found exactly that way: the whole of SessionInvalidationTests and
        // AuthEndpointTests went red in one step.
        //
        // Who the caller is, is a host-level decision: the API binds ICurrentUser to the cookie's
        // claims in its own Program.cs, and the worker binds it to this in its own. Infrastructure
        // supplies the implementation and picks neither.
        services.AddScoped<JobCurrentUser>();

        // The two reference jobs' ports. Logging adapters today; see JobAdapters.cs for what
        // replacing them costs (one class each, no caller affected).
        services.AddScoped<IOrderNotifier, LoggingOrderNotifier>();
        services.AddScoped<IOrderReportWriter, LoggingOrderReportWriter>();
        services.AddScoped<IOutboxRetention, OutboxRetention>();

        return services;
    }
}

/// <summary>
/// Turns <see cref="JobOptions.ParseQueues"/>'s throw into an options-validation failure, so a
/// bad <c>Jobs:Queues</c> value fails the host's startup with the offending name in the message.
/// </summary>
internal sealed class JobOptionsValidator : IValidateOptions<JobOptions>
{
    public ValidateOptionsResult Validate(string? name, JobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException exception)
        {
            // Narrow by type, not a bare catch: ParseQueues/ValidateSchedules throw exactly this
            // for a bad value, and anything else here is a genuine fault that must not be
            // reported as a configuration problem. CA1031 stays satisfied on its own terms.
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
