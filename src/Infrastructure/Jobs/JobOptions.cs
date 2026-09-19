using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.Jobs;

/// <summary>
/// Which lanes this host consumes, and how hard. Bound from the "Jobs" configuration section in
/// each host's Program.cs — not in AddJobs, for the reason CachingRegistration gives for
/// CacheOptions: binding there would make IOptions&lt;JobOptions&gt; depend on an IConfiguration
/// that a bare ServiceCollection in a unit test does not have.
/// </summary>
/// <remarks>
/// <b>The API leaves <see cref="Queues"/> empty and the worker sets it.</b> That one setting is
/// the whole host split: a host that lists no lane registers no listener and can only publish.
/// See ADR 0016.
/// </remarks>
public sealed class JobOptions
{
    /// <summary>
    /// The lanes to listen on, comma-separated — "light,heavy". Empty means publish-only.
    /// </summary>
    /// <remarks>
    /// A string rather than a string[] because it is set from a Kubernetes env value
    /// (<c>Jobs__Queues</c>, double underscores — a single one binds nothing and warns nothing),
    /// where an array would need <c>Jobs__Queues__0</c> per element.
    /// </remarks>
    public string Queues { get; set; } = string.Empty;

    /// <summary>How many light jobs may run at once, per pod.</summary>
    public int LightParallelism { get; set; } = 8;

    /// <summary>
    /// How many heavy jobs may run at once, per pod. Deliberately small: above the pod's core
    /// count, CPU-bound work only buys context switching and GC pressure. Scale replicas instead.
    /// </summary>
    public int HeavyParallelism { get; set; } = 2;

    /// <summary>
    /// Per-environment cron overrides, keyed by job type name —
    /// <c>Jobs__Schedules__PruneProcessedOutbox</c>. A job without an entry keeps the cron its
    /// registration declares. Get-only: the configuration binder fills an existing dictionary,
    /// confirmed against Microsoft.Extensions.Configuration.Binder 10.0.
    /// </summary>
    public IDictionary<string, string> Schedules { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="Queues"/> parsed, or throws naming the offending value. Used by both the
    /// listener registration and the options validation, so a name that parses here is a name
    /// that will be listened on — the two cannot disagree.
    /// </summary>
    public IReadOnlyList<JobLane> ParseQueues()
    {
        if (string.IsNullOrWhiteSpace(Queues))
        {
            return [];
        }

        return [.. Queues
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseLane)
            // "light,light" would otherwise attach two listeners to one queue. Harmless in
            // principle, but it doubles the lane's effective parallelism against a number an
            // operator set deliberately.
            .Distinct()];
    }

    /// <summary>
    /// Everything <c>AddJobs</c>'s options validation checks, in a form a host can call directly
    /// on a raw-bound instance. Throws naming the offending value.
    /// </summary>
    /// <remarks>
    /// Needed because the host reads these options at CONFIGURATION time — <c>UseWolverine</c>
    /// hooks the host builder, before any service provider exists — and so binds straight off
    /// <c>IConfiguration</c> rather than resolving <c>IOptions&lt;JobOptions&gt;</c>. Nothing in
    /// either host resolves that interface, so the <c>AddOptions</c> validators would never run on
    /// their own: <c>Jobs__LightParallelism=0</c> would reach
    /// <c>MaximumParallelMessages(0)</c> unchallenged, which is the "queue with no consumer and
    /// nothing to say so" failure the validation exists to prevent.
    /// </remarks>
    public void Validate()
    {
        if (LightParallelism < 1 || HeavyParallelism < 1)
        {
            throw new InvalidOperationException(
                "Jobs:LightParallelism and Jobs:HeavyParallelism must each be at least 1; " +
                $"got {LightParallelism} and {HeavyParallelism}. A lane at 0 registers a listener " +
                "that consumes nothing, with no error and no log.");
        }

        ParseQueues();
        ValidateSchedules();
    }

    /// <summary>
    /// An override must name a job that exists AND is scheduled, and carry a cron Quartz accepts.
    /// Anything else fails startup naming the key, rather than being ignored — an ignored override
    /// is a schedule an operator believes they changed.
    /// </summary>
    private void ValidateSchedules()
    {
        foreach (var (name, cron) in Schedules)
        {
            var job = Scheduling.JobSchedules.Find(name);

            if (job is null)
            {
                throw new InvalidOperationException(
                    $"Jobs:Schedules:{name} names no job. Scheduled jobs are: " +
                    $"{string.Join(", ", JobRegistration.Jobs.Where(j => j.IsScheduled).Select(j => j.Name))}.");
            }

            if (!job.IsScheduled)
            {
                throw new InvalidOperationException(
                    $"Jobs:Schedules:{name} is set, but {name} is not a scheduled job and cannot be " +
                    "made one by configuration.");
            }

            if (!Scheduling.JobSchedules.IsValidCron(cron))
            {
                throw new InvalidOperationException(
                    $"Jobs:Schedules:{name} = '{cron}' is not a Quartz cron expression " +
                    "(seconds first, e.g. '0 5 * * * ?').");
            }
        }
    }

    /// <summary>
    /// One lane name, or a throw naming it.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> alone is not enough.</b> It
    /// happily parses any numeric string, so <c>Jobs__Queues=7</c> would return <c>true</c> with
    /// <c>(JobLane)7</c> — validation would report success and the host would then die deeper in,
    /// in <c>JobRegistration.QueueFor</c>, with an <c>ArgumentOutOfRangeException</c> naming no
    /// configuration key at all. <see cref="Enum.IsDefined{TEnum}(TEnum)"/> is what makes the
    /// failure land here, where the message can name the offending value.
    /// </remarks>
    private static JobLane ParseLane(string name) =>
        Enum.TryParse<JobLane>(name, ignoreCase: true, out var lane) && Enum.IsDefined(lane)
            ? lane
            : throw new InvalidOperationException(
                $"'{name}' is not a job lane. Jobs:Queues accepts a comma-separated list of: " +
                $"{string.Join(", ", Enum.GetNames<JobLane>())}.");

    /// <summary>The parallelism for one lane. One place, so listener setup cannot drift.</summary>
    public int ParallelismFor(JobLane lane) => lane switch
    {
        JobLane.Light => LightParallelism,
        JobLane.Heavy => HeavyParallelism,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown job lane."),
    };
}
