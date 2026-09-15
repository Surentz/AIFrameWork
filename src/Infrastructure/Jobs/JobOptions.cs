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
            .Select(name => Enum.TryParse<JobLane>(name, ignoreCase: true, out var lane)
                ? lane
                : throw new InvalidOperationException(
                    $"'{name}' is not a job lane. Jobs:Queues accepts a comma-separated list of: " +
                    $"{string.Join(", ", Enum.GetNames<JobLane>())}."))];
    }

    /// <summary>The parallelism for one lane. One place, so listener setup cannot drift.</summary>
    public int ParallelismFor(JobLane lane) => lane switch
    {
        JobLane.Light => LightParallelism,
        JobLane.Heavy => HeavyParallelism,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown job lane."),
    };
}
