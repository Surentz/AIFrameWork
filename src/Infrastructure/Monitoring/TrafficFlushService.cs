using System.Data.Common;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// Moves closed traffic buckets from memory to Postgres, every few seconds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Runs in every host that records traffic</b> — the API and the worker both. Without it in
/// the worker, every command and query a job dispatches would be invisible. Each writes under its
/// own instance id, and the page sums them.
/// </para>
/// <para>
/// <b>The flush writes ABSOLUTE values and is therefore idempotent.</b> A cell is removed from
/// memory when it is taken, and only closed minutes are taken, so the row's counts are final at
/// the moment they are written. The upsert assigns rather than accumulates, which means a
/// statement retried by <c>EnableRetryOnFailure</c> after a lost acknowledgment writes the same
/// numbers again rather than doubling them — the failure mode an accumulating upsert would have.
/// </para>
/// <para>
/// <b>A flush failure loses that minute for that pod, and that is the accepted cost.</b> Counts
/// taken from memory are gone whether or not the write lands. Re-queueing them would mean holding
/// unbounded state against a database that is already unhappy; ADR 0021 takes the loss instead.
/// </para>
/// </remarks>
internal sealed partial class TrafficFlushService(
    IServiceScopeFactory scopes,
    TrafficRecorder recorder,
    ILogger<TrafficFlushService> logger) : BackgroundService
{
    /// <summary>
    /// Well under a minute, so a closed bucket lands soon after it closes rather than up to a
    /// minute later. The work is nothing when there is nothing to flush.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await FlushAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Flushes once. Internal so a test can drive it without waiting on the timer.</summary>
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        var buckets = recorder.TakeClosedBuckets();

        if (buckets.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();

        try
        {
            foreach (var bucket in buckets)
            {
                await UpsertAsync(context, bucket, cancellationToken).ConfigureAwait(false);
            }
        }
        // The two shapes "the database is not ready" arrives in, the same pair AdminReconciler
        // catches: a refusal the provider reports directly, and EF's execution strategy reporting
        // its own exhaustion on a transient one. Anything else is a programming error and surfaces.
        catch (DbException exception)
        {
            TrafficLog.FlushFailed(logger, buckets.Count, exception.Message);
        }
        catch (RetryLimitExceededException exception)
        {
            TrafficLog.FlushFailed(logger, buckets.Count, exception.Message);
        }
    }

    /// <summary>
    /// One upsert per cell. Assigns rather than accumulates — see the class remarks for why that
    /// is what makes a retried statement safe.
    /// </summary>
    private static Task<int> UpsertAsync(
        AiFrameworkDbContext context, TrafficBucket bucket, CancellationToken cancellationToken)
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
                "Bucket0" = EXCLUDED."Bucket0",
                "Bucket1" = EXCLUDED."Bucket1",
                "Bucket2" = EXCLUDED."Bucket2",
                "Bucket3" = EXCLUDED."Bucket3",
                "Bucket4" = EXCLUDED."Bucket4",
                "Bucket5" = EXCLUDED."Bucket5",
                "Bucket6" = EXCLUDED."Bucket6",
                "Bucket7" = EXCLUDED."Bucket7",
                "Bucket8" = EXCLUDED."Bucket8",
                "Bucket9" = EXCLUDED."Bucket9",
                "Bucket10" = EXCLUDED."Bucket10"
            """,
            cancellationToken);
    }
}

internal static partial class TrafficLog
{
    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "Traffic flush lost {BucketCount} bucket(s): {Reason}. "
            + "Those minutes are not recoverable for this pod.")]
    public static partial void FlushFailed(ILogger logger, int bucketCount, string reason);
}
