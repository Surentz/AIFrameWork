using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Jobs;
using FluentAssertions;
using ApplicationMarker = AiFramework.Application.AssemblyMarker;

namespace AiFramework.Infrastructure.Tests.Jobs;

/// <summary>
/// The job half of what <c>RegistrationCompletenessTests</c> does for commands and queries, and
/// the thing four other places in this repo already promise exists.
/// </summary>
/// <remarks>
/// <para>
/// <c>MapJobs</c> is an explicit, reflection-free list, which buys greppability at the cost of
/// compile-time safety. The failure mode of forgetting an entry is silent in the worst way: a job
/// with no routing rule has nowhere to go, and <c>IMessageBus.PublishAsync</c> discards a message
/// with no routes rather than throwing. Nothing logs, nothing fails, and the job simply never
/// runs.
/// </para>
/// <para>
/// This reflects over the Application assembly rather than Infrastructure's, exactly as
/// <c>RegistrationCompletenessTests</c> does, so a new <c>IJob</c> fails a test instead of a
/// production dispatch. <b>Do not delete it</b> — it is the only net under that mistake.
/// </para>
/// </remarks>
public sealed class JobRegistrationTests
{
    /// <summary>Every non-abstract <see cref="IJob"/> in the Application assembly.</summary>
    private static Type[] JobTypes() =>
        ApplicationMarker.Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                && typeof(IJob).IsAssignableFrom(t))
            .ToArray();

    [Fact]
    public void MapJobs_RoutesEveryJobInTheApplicationAssembly()
    {
        var jobs = JobTypes();

        // A completeness test over an empty set passes vacuously. If the marker ever resolves to
        // the wrong assembly, this scan silently finds nothing and the assertion below turns green
        // for the wrong reason — so the non-empty scan is asserted, not eyeballed. Same guard
        // RegistrationCompletenessTests puts on its domain-event scan.
        jobs.Should().NotBeEmpty(
            "the scan must find at least SendOrderConfirmation and RebuildOrderReport; an empty " +
            "result means ApplicationMarker resolved to the wrong assembly, not that there are " +
            "no jobs");

        // Reads the descriptor list rather than standing up a Wolverine host and inspecting its
        // routing internals: JobRegistration.Jobs is what MapJobs iterates, so this compares the
        // assembly against the exact list production uses.
        var routed = JobRegistration.Jobs.Select(job => job.JobType).ToHashSet();

        var unrouted = jobs.Where(job => !routed.Contains(job)).ToArray();

        unrouted.Should().BeEmpty(
            "every IJob needs an entry in JobRegistration.Jobs. Without one, MapJobs creates no " +
            "route, PublishAsync then DISCARDS the message — no exception, no log, and the " +
            "job never runs");
    }

    [Fact]
    public void EveryLane_MapsToADistinctQueueName()
    {
        var names = Enum.GetValues<JobLane>()
            .Select(JobRegistration.QueueFor)
            .ToArray();

        names.Should().OnlyHaveUniqueItems(
            "two lanes sharing a queue name silently merges them, so the heavy lane's work would " +
            "run at the light lane's parallelism");

        names.Should().AllSatisfy(
            name => name.Should().NotContain(
                "-",
                "the Postgres transport sanitises a queue name into an identifier, so a hyphen " +
                "here would not match the endpoint it actually creates (postgresql://jobs_light/)"));
    }

    [Fact]
    public void QueueFor_RejectsALaneThatIsNotDefined()
    {
        var undefined = (JobLane)999;

        var act = () => JobRegistration.QueueFor(undefined);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "an unmapped lane must fail loudly rather than return a null or empty queue name");
    }
}
