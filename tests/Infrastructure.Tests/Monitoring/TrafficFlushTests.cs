using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Monitoring;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using AiFramework.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Tests.Monitoring;

/// <summary>
/// Record, flush, read back — against a real database, because the upsert and the cross-pod
/// summation are the parts that only exist in SQL. See ADR 0021.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class TrafficFlushTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The upsert, run directly. The hosted service is a timer around exactly this statement, and
    /// driving the statement is what lets this test avoid waiting on one — tests/CLAUDE.md's
    /// no-sleep rule.
    /// </summary>
    private static Task<int> UpsertAsync(AiFrameworkDbContext context, TrafficBucket bucket)
    {
        var kind = bucket.Kind.ToString();

        return context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO traffic_buckets
                ("BucketStart", "Kind", "Name", "InstanceId", "Succeeded", "Failed", "Faulted",
                 "DurationMsTotal", "Bucket0", "Bucket1", "Bucket2", "Bucket3", "Bucket4",
                 "Bucket5", "Bucket6", "Bucket7", "Bucket8", "Bucket9", "Bucket10")
            VALUES
                ({bucket.BucketStart}, {kind}, {bucket.Name}, {bucket.InstanceId},
                 {bucket.Succeeded}, {bucket.Failed}, {bucket.Faulted}, {bucket.DurationMsTotal},
                 {bucket.Bucket0}, {bucket.Bucket1}, {bucket.Bucket2}, {bucket.Bucket3},
                 {bucket.Bucket4}, {bucket.Bucket5}, {bucket.Bucket6}, {bucket.Bucket7},
                 {bucket.Bucket8}, {bucket.Bucket9}, {bucket.Bucket10})
            ON CONFLICT ("BucketStart", "Kind", "Name", "InstanceId") DO UPDATE SET
                "Succeeded" = EXCLUDED."Succeeded",
                "Failed" = EXCLUDED."Failed",
                "Faulted" = EXCLUDED."Faulted",
                "DurationMsTotal" = EXCLUDED."DurationMsTotal",
                "Bucket0" = EXCLUDED."Bucket0"
            """);
    }

    private static TrafficBucket ABucket(
        DateTimeOffset at, string instanceId, string name, int succeeded, int fastCount) => new()
        {
            BucketStart = at,
            Kind = TrafficKind.Http,
            Name = name,
            InstanceId = instanceId,
            Succeeded = succeeded,
            DurationMsTotal = succeeded * 3L,
            Bucket0 = fastCount,
        };

    [Fact]
    public async Task TheUpsert_IsIdempotent()
    {
        var at = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var name = $"GET /api/idempotent-{Guid.NewGuid():N}";
        await using var context = _fixture.CreateContext();

        await UpsertAsync(context, ABucket(at, "pod-a", name, succeeded: 5, fastCount: 5));
        await UpsertAsync(context, ABucket(at, "pod-a", name, succeeded: 5, fastCount: 5));

        // ASSIGNS rather than accumulates, so a statement retried by EnableRetryOnFailure after a
        // lost acknowledgment writes the same numbers again rather than doubling them. The
        // recorder removes a cell when it takes it, so the values are final when written.
        var stored = await context.TrafficBuckets
            .AsNoTracking()
            .Where(b => b.Name == name)
            .SumAsync(b => b.Succeeded);

        stored.Should().Be(5);
    }

    [Fact]
    public async Task TheReader_SumsAcrossPods()
    {
        var at = new DateTimeOffset(2026, 9, 20, 13, 0, 0, TimeSpan.Zero);
        var name = $"GET /api/two-pods-{Guid.NewGuid():N}";
        await using var context = _fixture.CreateContext();

        await UpsertAsync(context, ABucket(at, "pod-a", name, succeeded: 4, fastCount: 4));
        await UpsertAsync(context, ABucket(at, "pod-b", name, succeeded: 6, fastCount: 6));

        var summary = await new TrafficReader(context)
            .SummarizeAsync(at.AddMinutes(-1), CancellationToken.None);

        // The property two API replicas depend on. Reading one pod's rows would report whichever
        // replica the operator's own session is pinned to (ADR 0010), which is stable enough to
        // look like the truth.
        var row = summary.Rows.Should().ContainSingle(r => r.Name == name).Subject;
        row.Total.Should().Be(10);
    }

    [Fact]
    public async Task TheReader_ReportsAPercentileFromTheSummedHistogram()
    {
        var at = new DateTimeOffset(2026, 9, 20, 14, 0, 0, TimeSpan.Zero);
        var name = $"GET /api/percentile-{Guid.NewGuid():N}";
        await using var context = _fixture.CreateContext();

        await UpsertAsync(context, ABucket(at, "pod-a", name, succeeded: 10, fastCount: 10));

        var summary = await new TrafficReader(context)
            .SummarizeAsync(at.AddMinutes(-1), CancellationToken.None);

        var row = summary.Rows.Should().ContainSingle(r => r.Name == name).Subject;
        row.P95Ms.Should().NotBeNull().And.BeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task TheRetentionSweep_DeletesOnlyWhatIsPastTheWindow()
    {
        var name = $"GET /api/retention-{Guid.NewGuid():N}";
        var now = new DateTimeOffset(2026, 9, 20, 15, 0, 0, TimeSpan.Zero);
        await using var context = _fixture.CreateContext();

        await UpsertAsync(context, ABucket(now.AddDays(-30), "pod-a", name, 1, 1));
        await UpsertAsync(context, ABucket(now.AddHours(-1), "pod-a", name, 1, 1));

        var clock = new TestClock { UtcNow = now };
        var options = Microsoft.Extensions.Options.Options.Create(new MonitoringOptions());
        await new TrafficRetention(context, options, clock).PruneAsync(CancellationToken.None);

        var remaining = await context.TrafficBuckets
            .AsNoTracking()
            .CountAsync(b => b.Name == name);

        // Seven days by default, so the month-old row goes and the hour-old one stays.
        remaining.Should().Be(1);
    }
}
