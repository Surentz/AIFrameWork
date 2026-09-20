using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// Reads <c>traffic_buckets</c>, summing across pods.
/// </summary>
/// <remarks>
/// <para>
/// <b>Summing across <c>InstanceId</c> is the whole point.</b> A row is one pod's minute; reading
/// one pod's rows would report whichever replica the operator's own session is pinned to, which
/// under ADR 0010's cookie affinity is stable enough to look like the truth. ADR 0021.
/// </para>
/// <para>
/// The aggregation is grouped in the database and only the grouped rows are materialised. The
/// percentile arithmetic happens in memory afterwards, because it is interpolation over already
/// summed counts rather than anything SQL should be asked to do.
/// </para>
/// </remarks>
internal sealed class TrafficReader(AiFrameworkDbContext context) : ITrafficReader
{
    public async Task<TrafficSummaryView> SummarizeAsync(
        DateTimeOffset since, CancellationToken cancellationToken)
    {
        var grouped = await context.TrafficBuckets
            .AsNoTracking()
            .Where(bucket => bucket.BucketStart >= since)
            .GroupBy(bucket => new { bucket.Kind, bucket.Name })
            .Select(group => new Totals
            {
                Kind = group.Key.Kind,
                Name = group.Key.Name,
                Succeeded = group.Sum(b => (long)b.Succeeded),
                Failed = group.Sum(b => (long)b.Failed),
                Faulted = group.Sum(b => (long)b.Faulted),
                DurationMsTotal = group.Sum(b => b.DurationMsTotal),
                B0 = group.Sum(b => (long)b.Bucket0),
                B1 = group.Sum(b => (long)b.Bucket1),
                B2 = group.Sum(b => (long)b.Bucket2),
                B3 = group.Sum(b => (long)b.Bucket3),
                B4 = group.Sum(b => (long)b.Bucket4),
                B5 = group.Sum(b => (long)b.Bucket5),
                B6 = group.Sum(b => (long)b.Bucket6),
                B7 = group.Sum(b => (long)b.Bucket7),
                B8 = group.Sum(b => (long)b.Bucket8),
                B9 = group.Sum(b => (long)b.Bucket9),
                B10 = group.Sum(b => (long)b.Bucket10),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = grouped
            .Select(ToRow)
            .OrderByDescending(row => row.Total)
            .ToList();

        var overall = ToRow(Combine(grouped));
        var minutes = Math.Max(1d, (DateTimeOffset.UtcNow - since).TotalMinutes);

        return new TrafficSummaryView(
            Since: since,
            RequestsPerMinute: overall.Total / minutes,
            ErrorRate: overall.Total == 0 ? 0d : (double)(overall.Failed + overall.Faulted) / overall.Total,
            Overall: overall,
            Rows: rows);
    }

    public async Task<TrafficSeriesView> SeriesAsync(
        DateTimeOffset since, CancellationToken cancellationToken)
    {
        var grouped = await context.TrafficBuckets
            .AsNoTracking()
            .Where(bucket => bucket.BucketStart >= since)
            .GroupBy(bucket => bucket.BucketStart)
            .Select(group => new Totals
            {
                Kind = TrafficKind.Http,
                Name = string.Empty,
                At = group.Key,
                Succeeded = group.Sum(b => (long)b.Succeeded),
                Failed = group.Sum(b => (long)b.Failed),
                Faulted = group.Sum(b => (long)b.Faulted),
                DurationMsTotal = group.Sum(b => b.DurationMsTotal),
                B0 = group.Sum(b => (long)b.Bucket0),
                B1 = group.Sum(b => (long)b.Bucket1),
                B2 = group.Sum(b => (long)b.Bucket2),
                B3 = group.Sum(b => (long)b.Bucket3),
                B4 = group.Sum(b => (long)b.Bucket4),
                B5 = group.Sum(b => (long)b.Bucket5),
                B6 = group.Sum(b => (long)b.Bucket6),
                B7 = group.Sum(b => (long)b.Bucket7),
                B8 = group.Sum(b => (long)b.Bucket8),
                B9 = group.Sum(b => (long)b.Bucket9),
                B10 = group.Sum(b => (long)b.Bucket10),
            })
            .OrderBy(totals => totals.At)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var points = grouped
            .Select(totals => new TrafficPointView(
                totals.At,
                totals.Total,
                totals.Failed,
                totals.Faulted,
                TrafficHistogram.Percentile(totals.Histogram, 0.95)))
            .ToList();

        return new TrafficSeriesView(since, points);
    }

    private static TrafficRowView ToRow(Totals totals) => new(
        totals.Kind,
        totals.Name,
        totals.Total,
        totals.Failed,
        totals.Faulted,
        totals.Total == 0 ? null : (double)totals.DurationMsTotal / totals.Total,
        TrafficHistogram.Percentile(totals.Histogram, 0.50),
        TrafficHistogram.Percentile(totals.Histogram, 0.95),
        TrafficHistogram.Percentile(totals.Histogram, 0.99));

    /// <summary>
    /// Every name folded into one row. Summing the HISTOGRAMS is what makes the overall p95 a real
    /// percentile rather than an average of per-name percentiles, which would be meaningless.
    /// </summary>
    private static Totals Combine(IReadOnlyList<Totals> all) => new()
    {
        Kind = TrafficKind.Http,
        Name = string.Empty,
        Succeeded = all.Sum(t => t.Succeeded),
        Failed = all.Sum(t => t.Failed),
        Faulted = all.Sum(t => t.Faulted),
        DurationMsTotal = all.Sum(t => t.DurationMsTotal),
        B0 = all.Sum(t => t.B0),
        B1 = all.Sum(t => t.B1),
        B2 = all.Sum(t => t.B2),
        B3 = all.Sum(t => t.B3),
        B4 = all.Sum(t => t.B4),
        B5 = all.Sum(t => t.B5),
        B6 = all.Sum(t => t.B6),
        B7 = all.Sum(t => t.B7),
        B8 = all.Sum(t => t.B8),
        B9 = all.Sum(t => t.B9),
        B10 = all.Sum(t => t.B10),
    };

    /// <summary>
    /// The shape the GroupBy projects into. A class with settable members rather than a record,
    /// because EF composes it in the SELECT and a positional record's constructor would not
    /// translate.
    /// </summary>
    private sealed class Totals
    {
        public TrafficKind Kind { get; init; }

        public string Name { get; init; } = string.Empty;

        public DateTimeOffset At { get; init; }

        public long Succeeded { get; init; }

        public long Failed { get; init; }

        public long Faulted { get; init; }

        public long DurationMsTotal { get; init; }

        public long B0 { get; init; }

        public long B1 { get; init; }

        public long B2 { get; init; }

        public long B3 { get; init; }

        public long B4 { get; init; }

        public long B5 { get; init; }

        public long B6 { get; init; }

        public long B7 { get; init; }

        public long B8 { get; init; }

        public long B9 { get; init; }

        public long B10 { get; init; }

        public long Total => Succeeded + Failed + Faulted;

        public long[] Histogram => [B0, B1, B2, B3, B4, B5, B6, B7, B8, B9, B10];
    }
}
