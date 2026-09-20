using AiFramework.Infrastructure.Monitoring;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Monitoring;

/// <summary>
/// The arithmetic the whole traffic view rests on. A mean cannot give a percentile and per-pod
/// means cannot be averaged into one, so this is what makes p95 across two replicas a real
/// number. See ADR 0021.
/// </summary>
public sealed class TrafficHistogramTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(5000, 9)]
    [InlineData(5001, 10)]
    [InlineData(long.MaxValue, 10)]
    public void IndexFor_PutsADurationInItsBucket(long elapsedMs, int expected)
    {
        // A value ON a boundary belongs to that bucket, not the next one. Worth pinning: the
        // bounds are a stored contract, and rows already written were counted against them.
        TrafficHistogram.IndexFor(elapsedMs).Should().Be(expected);
    }

    [Fact]
    public void Percentile_WithNoMeasurements_IsNull()
    {
        var empty = new long[TrafficHistogram.BucketCount];

        // Null, not zero. "No requests" and "every request took 0ms" are different answers, and a
        // tile reading 0ms p95 when nothing happened is a lie an operator would act on.
        TrafficHistogram.Percentile(empty, 0.95).Should().BeNull();
    }

    [Fact]
    public void Percentile_WithEverythingInOneBucket_LandsInThatBucket()
    {
        var counts = new long[TrafficHistogram.BucketCount];
        counts[2] = 100;

        // Bucket 2 spans 10ms (exclusive) to 25ms (inclusive).
        var p95 = TrafficHistogram.Percentile(counts, 0.95);

        p95.Should().BeInRange(10, 25);
    }

    [Fact]
    public void Percentile_IsDrivenByTheTail_NotByTheBulk()
    {
        var counts = new long[TrafficHistogram.BucketCount];
        counts[0] = 950;
        counts[8] = 50;

        var p50 = TrafficHistogram.Percentile(counts, 0.50);
        var p99 = TrafficHistogram.Percentile(counts, 0.99);

        // The exact reason a mean is not a substitute. Nearly all of this traffic is under five
        // milliseconds, while a mean would report a couple of hundred - a number describing
        // nobody's experience. The median stays in the fast bucket; the ninety-ninth lands in
        // the slow one.
        p50.Should().BeLessThanOrEqualTo(5);
        p99.Should().BeGreaterThan(1000);
    }

    [Fact]
    public void Percentile_LandingInTheOverflowBucket_ReportsTheTopBound()
    {
        var counts = new long[TrafficHistogram.BucketCount];
        counts[TrafficHistogram.BucketCount - 1] = 10;

        // The overflow bucket has no upper bound, so the top bound is the honest answer - at
        // least this slow. Inventing a number above it would not be.
        TrafficHistogram.Percentile(counts, 0.95)
            .Should().Be(TrafficHistogram.Bounds[^1]);
    }

    [Fact]
    public void Percentile_IsMonotonic()
    {
        var counts = new long[TrafficHistogram.BucketCount];
        counts[1] = 10;
        counts[4] = 10;
        counts[7] = 10;

        var p50 = TrafficHistogram.Percentile(counts, 0.50)!.Value;
        var p95 = TrafficHistogram.Percentile(counts, 0.95)!.Value;
        var p99 = TrafficHistogram.Percentile(counts, 0.99)!.Value;

        p50.Should().BeLessThanOrEqualTo(p95);
        p95.Should().BeLessThanOrEqualTo(p99);
    }

    [Fact]
    public void Percentile_SumsTheSameWhetherOneSourceOrTwo()
    {
        var onePod = new long[TrafficHistogram.BucketCount];
        onePod[3] = 60;
        onePod[6] = 40;

        var first = new long[TrafficHistogram.BucketCount];
        first[3] = 30;
        first[6] = 10;
        var second = new long[TrafficHistogram.BucketCount];
        second[3] = 30;
        second[6] = 30;
        var summed = first.Zip(second, (a, b) => a + b).ToArray();

        // The property that makes two API replicas reportable at all: summing counts and then
        // interpolating gives the same answer as one pod having done all the work. Averaging two
        // pods' percentiles would not.
        TrafficHistogram.Percentile(summed, 0.95)
            .Should().Be(TrafficHistogram.Percentile(onePod, 0.95));
    }
}
