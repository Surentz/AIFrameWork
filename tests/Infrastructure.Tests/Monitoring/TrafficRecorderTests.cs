using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Monitoring;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Monitoring;

public sealed class TrafficRecorderTests
{
    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } =
            new(2026, 9, 20, 12, 0, 30, TimeSpan.Zero);
    }

    [Fact]
    public void TakeClosedBuckets_LeavesTheCurrentMinuteAlone()
    {
        var clock = new TestClock();
        var recorder = new TrafficRecorder(clock);

        recorder.Record(TrafficKind.Http, "GET /api/orders", TrafficOutcome.Succeeded, 12);

        // The minute is still open, so its counts are still arriving. Flushing it now would write
        // a partial row and then a second, larger one - and the upsert assigns rather than adds,
        // so the partial would be the one that stuck if the later flush were ever lost.
        recorder.TakeClosedBuckets().Should().BeEmpty();
    }

    [Fact]
    public void TakeClosedBuckets_ReturnsAMinuteOnceItHasClosed()
    {
        var clock = new TestClock();
        var recorder = new TrafficRecorder(clock);

        recorder.Record(TrafficKind.Http, "GET /api/orders", TrafficOutcome.Succeeded, 12);
        recorder.Record(TrafficKind.Http, "GET /api/orders", TrafficOutcome.Failed, 8);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);

        var taken = recorder.TakeClosedBuckets();

        var bucket = taken.Should().ContainSingle().Subject;
        bucket.Name.Should().Be("GET /api/orders");
        bucket.Succeeded.Should().Be(1);
        bucket.Failed.Should().Be(1);
        bucket.DurationMsTotal.Should().Be(20);
    }

    [Fact]
    public void TakeClosedBuckets_TakesEachMinuteOnlyOnce()
    {
        var clock = new TestClock();
        var recorder = new TrafficRecorder(clock);
        recorder.Record(TrafficKind.Query, "GetOrders", TrafficOutcome.Succeeded, 3);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);

        recorder.TakeClosedBuckets().Should().ContainSingle();

        // Removing on take is what lets the flush write ABSOLUTE values: the row's counts are
        // final at the moment they are written, so a statement retried after a lost
        // acknowledgment writes the same numbers rather than doubling them.
        recorder.TakeClosedBuckets().Should().BeEmpty();
    }

    [Fact]
    public void TakeClosedBuckets_SeparatesKindsAndNames()
    {
        var clock = new TestClock();
        var recorder = new TrafficRecorder(clock);

        recorder.Record(TrafficKind.Http, "GET /api/orders", TrafficOutcome.Succeeded, 1);
        recorder.Record(TrafficKind.Query, "GetOrders", TrafficOutcome.Succeeded, 1);
        recorder.Record(TrafficKind.Http, "POST /api/orders", TrafficOutcome.Succeeded, 1);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);

        recorder.TakeClosedBuckets().Should().HaveCount(3);
    }

    [Fact]
    public void Record_WithABlankName_IsIgnored()
    {
        var clock = new TestClock();
        var recorder = new TrafficRecorder(clock);

        // A request that matched no endpoint has no template. Recording it under an empty name
        // would merge every unmatched URL into one meaningless row.
        recorder.Record(TrafficKind.Http, "  ", TrafficOutcome.Failed, 1);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);

        recorder.TakeClosedBuckets().Should().BeEmpty();
    }

    [Fact]
    public void Record_CountsIntoTheRightHistogramBucket()
    {
        var clock = new TestClock();
        var recorder = new TrafficRecorder(clock);

        recorder.Record(TrafficKind.Http, "GET /api/orders", TrafficOutcome.Succeeded, 3);
        recorder.Record(TrafficKind.Http, "GET /api/orders", TrafficOutcome.Succeeded, 9_000);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);

        var bucket = recorder.TakeClosedBuckets().Should().ContainSingle().Subject;

        bucket.Bucket0.Should().Be(1, "3ms falls in the first bucket");
        bucket.Bucket10.Should().Be(1, "9s is slower than the top bound, so it overflows");
    }

    [Fact]
    public void Record_IsSafeFromManyThreadsAtOnce()
    {
        var clock = new TestClock();
        var recorder = new TrafficRecorder(clock);

        // It runs inline on every request across the whole thread pool, with no lock by design.
        Parallel.For(0, 1_000, _ =>
            recorder.Record(TrafficKind.Http, "GET /api/orders", TrafficOutcome.Succeeded, 1));

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var bucket = recorder.TakeClosedBuckets().Should().ContainSingle().Subject;

        bucket.Succeeded.Should().Be(1_000, "no count may be lost to a race");
    }
}
