using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Diagnostics;
using Quartz.Extensibility;

namespace AiFramework.Infrastructure.Jobs.Scheduling;

/// <summary>
/// The worker's scheduler. Called from src/Worker/Program.cs ONLY — the API never starts one.
/// </summary>
public static class QuartzRegistration
{
    /// <summary>
    /// Schema-qualified: Quartz's tables live in their own `quartz` schema, beside `wolverine` and
    /// `wolverine_queues`, apart from EF's `public`. Must match the AddQuartzSchema migration.
    /// </summary>
    public const string TablePrefix = "quartz.qrtz_";

    /// <summary>
    /// Quartz's tracing source, for the worker's tracer to subscribe to — re-exported so no host
    /// has to name a Quartz type itself.
    /// </summary>
    public const string ActivitySourceName = QuartzInstrumentation.ActivitySourceName;

    public static IServiceCollection AddJobScheduling(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddQuartz(q =>
        {
            q.UsePersistentStore(store =>
            {
                store.UsePostgres(connectionString);
                store.ConfigureStore(o =>
                {
                    o.TablePrefix = TablePrefix;

                    // Quartz's own default, set explicitly because it is load-bearing: the EF
                    // migration owns this schema, and a worker must refuse to start against a
                    // missing or stale one rather than create it behind the migration's back.
                    o.SchemaProvisioning = SchemaProvisioning.Validate;
                });

                // Forced, not chosen: a trigger must fire on exactly one worker, and a pause must
                // apply to all of them.
                store.UseClustering(c => c.Enabled = true);
            });

            // Clustering needs each node to tell itself apart from the others. Left unconfigured,
            // Quartz 4.1 uses the literal instance id "NON_CLUSTERED" for every node (Task 1's
            // spike finding), which would make two worker pods look like one cluster node and
            // defeat both single-fire and failover.
            //
            // Quartz's built-in generators (SimpleInstanceIdGenerator, HostNameInstanceIdGenerator)
            // are `internal` in 4.1.0 (CS0122), so neither can be passed to
            // UseInstanceIdGenerator<T>(). Quartz 4.1 also documents
            // QuartzSchedulerOptions.GenerateInstanceId, which should select its own
            // SimpleInstanceIdGenerator; that switch was not the one proven against the shipped
            // package, so it is not what is used here. ProcessInstanceIdGenerator below is, and
            // SchedulingTests asserts the result: an id that is not the sentinel, and different
            // for two hosts on the same store. In Kubernetes the hostname alone already differs
            // per pod; the process id and tick count are for two workers on one machine.
            q.UseInstanceIdGenerator<ProcessInstanceIdGenerator>();

            q.AddQuartzHealthChecks();
        });

        services.AddSingleton<ScheduleSynchronizer>();
        services.AddHostedService<ScheduleSynchronizerService>();

        // Registered AFTER the synchronizer's hosted service, so the store matches the code before
        // the scheduler begins acquiring triggers.
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

        return services;
    }

    private sealed class ScheduleSynchronizerService(ScheduleSynchronizer synchronizer) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            synchronizer.SynchronizeAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Stands in for Quartz's own <c>SimpleInstanceIdGenerator</c>, which 4.1.0 keeps
    /// <c>internal</c> and so cannot be named from outside the Quartz assembly — see the comment
    /// where this is registered. Reproduces the same documented shape, hostname plus a
    /// time-derived value, so this node's id in <c>QRTZ_SCHEDULER_STATE</c> stays as legible to an
    /// operator as the built-in generator's own would have been, and adds the process id: two
    /// worker processes started in the same tick on one machine — plausible for a local dev script
    /// that launches both in a loop — would otherwise share an instance id, which is exactly the
    /// "looks like one cluster node" failure this generator exists to avoid.
    /// </summary>
    private sealed class ProcessInstanceIdGenerator : IInstanceIdGenerator
    {
        public ValueTask<string> GenerateInstanceId(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                $"{Environment.MachineName}-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}");
    }
}
