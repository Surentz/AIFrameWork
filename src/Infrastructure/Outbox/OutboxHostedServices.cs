using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>
/// Claims due rows and hands them to the workers. FullMode.Wait is the point of using a
/// channel: when the workers saturate, WriteAsync blocks, the poller stops claiming, and the
/// DATABASE stays the buffer instead of memory.
/// </summary>
public sealed partial class OutboxPollerService(
    IServiceScopeFactory scopeFactory,
    ChannelWriter<OutboxWorkItem> writer,
    IOptions<OutboxOptions> options,
    ILogger<OutboxPollerService> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The whole loop lives inside this try so writer.Complete() below runs on every exit
        // path. Every await RunPollCycleAsync or Task.Delay takes stoppingToken, so on shutdown
        // they THROW OperationCanceledException rather than returning — the while condition is
        // essentially never the reason the loop exits. Without this try/finally, the channel is
        // never completed and reader.ReadAllAsync in the workers never sees a graceful end (it
        // only stops because its own stoppingToken fires) — which happens to work, but documents
        // an intent the code does not implement.
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var claimed = await RunPollCycleAsync(stoppingToken).ConfigureAwait(false);

                if (claimed == 0)
                {
                    // Also the delay after a caught failure (RunPollCycleAsync returns 0 for
                    // one): it keeps a persistent failure spinning at poll rate instead of a
                    // tight CPU-bound loop.
                    await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            writer.Complete();
            LogStopped(logger);
        }
    }

    /// <summary>
    /// One poll cycle: claim, hand each item to the workers, prune if nothing was claimed.
    /// Returns the number claimed so the caller knows whether to pause before the next cycle.
    /// </summary>
    private async Task<int> RunPollCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var poller = scope.ServiceProvider.GetRequiredService<OutboxPoller>();
            var batch = await poller.ClaimAsync(stoppingToken).ConfigureAwait(false);

            foreach (var item in batch)
            {
                await writer.WriteAsync(item, stoppingToken).ConfigureAwait(false);
            }

            if (batch.Count == 0)
            {
                await poller.PruneAsync(stoppingToken).ConfigureAwait(false);
            }

            return batch.Count;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a failure — must not be logged or retried, so it is rethrown
            // before the general catch below can see it.
            throw;
        }
        catch (Exception exception)
        {
            // BackgroundServiceExceptionBehavior.StopHost is the default since .NET 6: an
            // exception left to propagate out of ExecuteAsync takes down the entire host, not
            // just the outbox. ClaimAsync opens a database connection and runs raw SQL, so a
            // transient Postgres failure — a dropped connection, a failover, the database not
            // yet accepting connections at startup — must not be allowed to do that. This
            // catch, and the worker's below, are what the file-scoped CA1031 exemption in
            // .editorconfig exists for: a worker loop has no IExceptionHandler-style out that
            // receives the exception as a parameter instead of catching it.
            LogPollCycleFailed(logger, exception);
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox poll cycle failed.")]
    private static partial void LogPollCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox poller stopped.")]
    private static partial void LogStopped(ILogger logger);
}

/// <summary>Drains the channel. WorkerCount loops share one reader; the channel distributes.</summary>
public sealed partial class OutboxWorkerService(
    IServiceScopeFactory scopeFactory,
    ChannelReader<OutboxWorkItem> reader,
    IOptions<OutboxOptions> options,
    ILogger<OutboxWorkerService> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, _options.WorkerCount)
            .Select(_ => RunAsync(stoppingToken)));

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await ProcessOneAsync(item, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One item's full cycle: scope creation, resolution, processing, and scope disposal, all
    /// under one catch, mirroring OutboxPollerService.RunPollCycleAsync. Scope creation and
    /// GetRequiredService can throw too (a broken registration, a constructor that throws), and
    /// if either sat outside this try, that exception would escape RunAsync, then
    /// Task.WhenAll, then ExecuteAsync, and BackgroundServiceExceptionBehavior.StopHost would
    /// take the whole host down over one item. The await-using stays INSIDE the try
    /// deliberately: its dispose still runs before an exception reaches the catch below, since
    /// await-using compiles to a try/finally of its own around the rest of this block, so the
    /// failure path disposes the scope rather than leaking it.
    /// </summary>
    private async Task ProcessOneAsync(OutboxWorkItem item, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<OutboxWorkItemProcessor>();

            await processor.ProcessAsync(item, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A worker loop must survive anything a handler, resolution, the database, or scope
            // disposal throws. Without this, one bad message - or one broken registration -
            // kills this worker, and the pool dies one worker at a time until nothing drains,
            // with no record against the row that caused it. The processor already records
            // handler failures; this catches what escapes it: a resolution failure, a database
            // error while recording the outcome, or a throwing DisposeAsync. See .editorconfig
            // for the file-scoped CA1031 exemption this requires - shared with
            // OutboxPollerService's catch, which needs it for the equally serious reason that
            // an unhandled exception there would stop the entire host, not just the outbox.
            LogWorkItemFailed(logger, exception, item.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox item {MessageId} failed outside the processor.")]
    private static partial void LogWorkItemFailed(ILogger logger, Exception exception, Guid messageId);
}
