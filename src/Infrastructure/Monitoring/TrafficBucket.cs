using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// One minute of one kind of work, on one pod. Keyed on all four of those, because the page SUMS
/// across pods — which is what makes two API replicas report the application's traffic rather
/// than whichever pod happened to answer. See ADR 0021 and ADR 0018.
/// </summary>
public sealed class TrafficBucket
{
    /// <summary>Truncated to the minute, UTC. Approximate across pods: clocks skew.</summary>
    public required DateTimeOffset BucketStart { get; init; }

    public required TrafficKind Kind { get; init; }

    /// <summary>A route template for HTTP, a request type's name otherwise. Never a raw path.</summary>
    public required string Name { get; init; }

    /// <summary><c>HOSTNAME</c>, matching what ObservabilityRegistration uses for its instance id.</summary>
    public required string InstanceId { get; init; }

    public int Succeeded { get; set; }

    public int Failed { get; set; }

    public int Faulted { get; set; }

    /// <summary>For the mean. The percentiles come from the histogram, which a mean cannot give.</summary>
    public long DurationMsTotal { get; set; }

    // One column per histogram bucket. Columns rather than an array or JSON because the whole
    // point is that Postgres sums them across pods in the query that reads them.
    public int Bucket0 { get; set; }

    public int Bucket1 { get; set; }

    public int Bucket2 { get; set; }

    public int Bucket3 { get; set; }

    public int Bucket4 { get; set; }

    public int Bucket5 { get; set; }

    public int Bucket6 { get; set; }

    public int Bucket7 { get; set; }

    public int Bucket8 { get; set; }

    public int Bucket9 { get; set; }

    /// <summary>Everything slower than the top bound.</summary>
    public int Bucket10 { get; set; }
}
