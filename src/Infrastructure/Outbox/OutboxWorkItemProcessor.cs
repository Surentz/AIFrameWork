using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>
/// Processes exactly one claimed item and records the outcome. All the decision logic lives
/// here rather than in the worker loop, so it is testable without a host or a channel.
/// </summary>
public sealed class OutboxWorkItemProcessor(
    AiFrameworkDbContext context,
    DomainEventRegistry registry,
    IServiceProvider services,
    IOptions<OutboxOptions> options,
    IClock clock)
{
    private readonly OutboxOptions _options = options.Value;

    public async Task ProcessAsync(OutboxWorkItem item, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(item.EventName, out var descriptor))
        {
            await CompleteAsync(item, OutboxStatus.Dead,
                $"No registration for event name '{item.EventName}'.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await descriptor.Dispatch(
                services, item.Payload, new DomainEventContext(item.Id, item.Attempt), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailAsync(item, exception, cancellationToken).ConfigureAwait(false);
            return;
        }

        await CompleteAsync(item, OutboxStatus.Processed, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailAsync(OutboxWorkItem item, Exception exception, CancellationToken cancellationToken)
    {
        if (item.Attempt >= _options.MaxAttempts)
        {
            await CompleteAsync(item, OutboxStatus.Dead, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Exponential backoff with jitter, capped. Jitter stops a batch that failed together
        // from retrying in lockstep and re-colliding on whatever caused the failure.
        var seconds = Math.Min(Math.Pow(2, item.Attempt), _options.MaxBackoff.TotalSeconds);
        var jitter = Random.Shared.NextDouble() * seconds * 0.2;
        var nextAttemptAt = clock.UtcNow.AddSeconds(seconds + jitter);

        await context.Outbox.Where(m => m.Id == item.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                .SetProperty(m => m.LeasedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LastError, Truncate(exception.Message)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    // ProcessedAt records when the row reached a terminal state, not that the handler
    // succeeded — it is stamped for Dead rows as well as Processed ones. PruneAsync only ever
    // deletes rows whose status is Processed, so a dead row's ProcessedAt does not put it at
    // risk of pruning; it exists so a dead-lettered row still records when retries stopped.
    private Task<int> CompleteAsync(
        OutboxWorkItem item, OutboxStatus status, string? error, CancellationToken cancellationToken)
    {
        var processedAt = clock.UtcNow;

        return context.Outbox.Where(m => m.Id == item.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, status)
                .SetProperty(m => m.ProcessedAt, processedAt)
                .SetProperty(m => m.LeasedUntil, (DateTimeOffset?)null)
                .SetProperty(m => m.LastError, error == null ? null : Truncate(error)),
                cancellationToken);
    }

    private static string Truncate(string value) =>
        value.Length <= 2048 ? value : value[..2048];
}
