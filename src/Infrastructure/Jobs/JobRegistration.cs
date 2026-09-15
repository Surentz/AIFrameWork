using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;

namespace AiFramework.Infrastructure.Jobs;

/// <summary>
/// The job framework's wiring: lane-to-queue mapping, the explicit job list, the listeners a host
/// opts into, and the retry policy. See ADR 0016.
/// </summary>
public static class JobRegistration
{
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
    /// <b>Every <see cref="IJob"/> in the Application assembly must appear here.</b> Explicit and
    /// greppable, mirroring <c>AddMessaging()</c>, and <c>JobRegistrationTests</c> fails the build
    /// when one is missing — the only thing standing in for compile-time safety, exactly as
    /// <c>RegistrationCompletenessTests</c> is for commands and queries.
    /// </summary>
    /// <remarks>
    /// Runs on EVERY host, including the API. Routing rules are how a job gets published; they
    /// say nothing about who handles it. The listen side below is the host split.
    /// </remarks>
    public static void MapJobs(WolverineOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        PublishJob<SendOrderConfirmation>(opts);
        PublishJob<RebuildOrderReport>(opts);
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
            .IncludeType<RebuildOrderReportHandler>();

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
                "JobOptions parallelism must be at least 1 for every lane.");

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
        // caller. Found exactly that way: the whole of SessionInvalidationTests and AuthEndpointTests
        // went red in one step.
        //
        // Who the caller is, is a host-level decision: the API binds ICurrentUser to the cookie's
        // claims in its own Program.cs, and the worker binds it to this in its own. Infrastructure
        // supplies the implementation and picks neither.
        services.AddScoped<JobCurrentUser>();

        // The two reference jobs' ports. Logging adapters today; see JobAdapters.cs for what
        // replacing them costs (one class each, no caller affected).
        services.AddScoped<IOrderNotifier, LoggingOrderNotifier>();
        services.AddScoped<IOrderReportWriter, LoggingOrderReportWriter>();

        return services;
    }

    private static void PublishJob<TJob>(WolverineOptions opts)
        where TJob : IJob =>
        opts.PublishMessage<TJob>().ToPostgresqlQueue(QueueFor(TJob.Lane));
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
            options.ParseQueues();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException exception)
        {
            // Narrow by type, not a bare catch: ParseQueues throws exactly this for an unknown
            // lane, and anything else here is a genuine fault that must not be reported as a
            // configuration problem. CA1031 stays satisfied on its own terms.
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
