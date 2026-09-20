using System.Collections.Concurrent;
using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>One accumulating (minute, kind, name) cell.</summary>
internal sealed record TrafficKey(DateTimeOffset BucketStart, TrafficKind Kind, string Name);

/// <summary>
/// Counters for one cell. Mutated by many threads through interlocked adds, never by lock.
/// </summary>
internal sealed class TrafficCounters
{
    private readonly long[] _histogram = new long[TrafficHistogram.BucketCount];

    private long _succeeded;
    private long _failed;
    private long _faulted;
    private long _durationMsTotal;

    public void Add(TrafficOutcome outcome, long elapsedMs)
    {
        switch (outcome)
        {
            case TrafficOutcome.Succeeded:
                Interlocked.Increment(ref _succeeded);
                break;
            case TrafficOutcome.Failed:
                Interlocked.Increment(ref _failed);
                break;
            default:
                Interlocked.Increment(ref _faulted);
                break;
        }

        Interlocked.Add(ref _durationMsTotal, elapsedMs);
        Interlocked.Increment(ref _histogram[TrafficHistogram.IndexFor(elapsedMs)]);
    }

    /// <summary>
    /// A snapshot for the flush. Read without a lock: a cell is only ever read after its minute
    /// has closed, so a concurrent write here is a straggler whose timestamp already put it in
    /// this bucket — worth at most one count, and not worth a lock on every request to avoid.
    /// </summary>
    public (long Succeeded, long Failed, long Faulted, long DurationMsTotal, long[] Histogram) Read() =>
        (Interlocked.Read(ref _succeeded),
         Interlocked.Read(ref _failed),
         Interlocked.Read(ref _faulted),
         Interlocked.Read(ref _durationMsTotal),
         [.. _histogram]);
}

/// <summary>
/// The in-memory half of traffic recording: counts per (minute, kind, name), flushed by
/// <see cref="TrafficFlushService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton, and deliberately so.</b> It accumulates across requests; a scoped one would
/// count nothing. That also means it must be safe for concurrent use with no locking on the hot
/// path — hence interlocked counters rather than a lock per record.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> <see cref="Record"/> runs inline on every request; an
/// observability concern that can fail the work it observes is worse than no observability.
/// </para>
/// </remarks>
internal sealed class TrafficRecorder(IClock clock) : ITrafficRecorder
{
    private readonly ConcurrentDictionary<TrafficKey, TrafficCounters> _cells = new();

    /// <summary>
    /// The pod this process is. Null outside a container, where the SDK would generate one —
    /// recorded as a literal so a single-process dev run still groups under one name.
    /// </summary>
    public static string InstanceId { get; } =
        Environment.GetEnvironmentVariable("HOSTNAME") is { Length: > 0 } host ? host : "local";

    public void Record(TrafficKind kind, string name, TrafficOutcome outcome, long elapsedMs)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var key = new TrafficKey(Truncate(clock.UtcNow), kind, name);

        _cells.GetOrAdd(key, static _ => new TrafficCounters()).Add(outcome, elapsedMs);
    }

    /// <summary>
    /// Removes and returns every cell whose minute has closed, leaving the current minute alone.
    /// Removing is what makes the flush write absolute values that are safe to retry.
    /// </summary>
    public IReadOnlyList<TrafficBucket> TakeClosedBuckets()
    {
        var currentMinute = Truncate(clock.UtcNow);
        var taken = new List<TrafficBucket>();

        foreach (var key in _cells.Keys.Where(key => key.BucketStart < currentMinute).ToList())
        {
            if (!_cells.TryRemove(key, out var counters))
            {
                continue;
            }

            var (succeeded, failed, faulted, durationTotal, histogram) = counters.Read();

            taken.Add(new TrafficBucket
            {
                BucketStart = key.BucketStart,
                Kind = key.Kind,
                Name = key.Name,
                InstanceId = InstanceId,
                Succeeded = (int)succeeded,
                Failed = (int)failed,
                Faulted = (int)faulted,
                DurationMsTotal = durationTotal,
                Bucket0 = (int)histogram[0],
                Bucket1 = (int)histogram[1],
                Bucket2 = (int)histogram[2],
                Bucket3 = (int)histogram[3],
                Bucket4 = (int)histogram[4],
                Bucket5 = (int)histogram[5],
                Bucket6 = (int)histogram[6],
                Bucket7 = (int)histogram[7],
                Bucket8 = (int)histogram[8],
                Bucket9 = (int)histogram[9],
                Bucket10 = (int)histogram[10],
            });
        }

        return taken;
    }

    /// <summary>
    /// To the minute, in UTC. Approximate across pods by construction — clocks skew, and ADR 0021
    /// accepts that rather than pretending a bucket boundary means the same instant everywhere.
    /// Nothing may be built on this that needs finer resolution than a minute.
    /// </summary>
    private static DateTimeOffset Truncate(DateTimeOffset instant) =>
        new(instant.Year, instant.Month, instant.Day, instant.Hour, instant.Minute, 0, TimeSpan.Zero);
}
