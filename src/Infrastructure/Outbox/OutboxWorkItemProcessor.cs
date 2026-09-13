using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>
/// Processes exactly one claimed item and records the outcome. All the decision logic lives
/// here rather than in the worker loop, so it is testable without a host or a channel.
/// </summary>
public sealed partial class OutboxWorkItemProcessor(
    AiFrameworkDbContext context,
    DomainEventRegistry registry,
    IServiceProvider services,
    IOptions<OutboxOptions> options,
    IClock clock,
    ILogger<OutboxWorkItemProcessor> logger)
{
    private readonly OutboxOptions _options = options.Value;

    public async Task ProcessAsync(OutboxWorkItem item, CancellationToken cancellationToken)
    {
        // The raw ILogger.BeginScope<TState> overload, not the LoggerExtensions.BeginScope(this
        // ILogger, string, object?[]) convenience one — CA1848 flags the latter the same way it
        // flags LogInformation/LogWarning called directly instead of through a [LoggerMessage]
        // delegate. Mirrors Behaviors.LoggedAsync's identical scope on the command/query
        // dispatch path. MessageId is stable across every redelivery of the same event (see
        // src/Application/CLAUDE.md), which is what makes it the key for "show me every attempt
        // at this message" across the three log calls below.
        using var scope = logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["MessageId"] = item.Id,
            ["EventType"] = item.EventName,
            ["Attempt"] = item.Attempt,
        });

        if (!registry.TryGet(item.EventName, out var descriptor))
        {
            var reason = $"No registration for event name '{item.EventName}'.";
            await CompleteAsync(item, OutboxStatus.Dead, reason, cancellationToken)
                .ConfigureAwait(false);
            LogDeadLettered(logger, item.Id, item.EventName, reason);
            return;
        }

        try
        {
            await descriptor.Dispatch(
                services, item.Payload, new DomainEventContext(item.Id, item.Attempt), cancellationToken)
                .ConfigureAwait(false);
        }
        // OperationCanceledException is deliberately NOT caught here — it propagates, leaving
        // the row InFlight with its lease still set. OutboxPoller.ClaimAsync reclaims any
        // InFlight row whose LeasedUntil has passed, so a cancelled (e.g. shutting-down) worker
        // does not need to record a failure itself; the row is picked up again once the lease
        // expires. Nothing is logged on this path either, for the same reason: it is not this
        // item's outcome, it is the process shutting down.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailAsync(item, exception, cancellationToken).ConfigureAwait(false);
            return;
        }

        await CompleteAsync(item, OutboxStatus.Processed, null, cancellationToken).ConfigureAwait(false);
        LogDispatched(logger, item.Id, item.EventName);
    }

    private async Task FailAsync(OutboxWorkItem item, Exception exception, CancellationToken cancellationToken)
    {
        if (item.Attempt >= _options.MaxAttempts)
        {
            await CompleteAsync(item, OutboxStatus.Dead, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            LogDeadLettered(logger, item.Id, item.EventName, exception.Message);
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

        LogRetryScheduled(
            logger, item.Id, item.EventName, item.Attempt, nextAttemptAt, exception.Message);
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

    // Debug, matching Behaviors.LoggedAsync's MessagingLog.Succeeded: the routine outcome stays
    // quiet by default, so the store is not filled with "it worked" for every delivered event.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox message {MessageId} ({EventType}) dispatched.")]
    private static partial void LogDispatched(ILogger logger, Guid messageId, string eventType);

    // Information, not Warning: a retry is the self-healing path working as designed (a
    // transient database blip, say), worth knowing without paging anyone. Dead-lettering below
    // is the one that should.
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Outbox message {MessageId} ({EventType}) failed on attempt {Attempt}; " +
            "retrying at {NextAttemptAt}. {Reason}")]
    private static partial void LogRetryScheduled(
        ILogger logger,
        Guid messageId,
        string eventType,
        int attempt,
        DateTimeOffset nextAttemptAt,
        string reason);

    // Warning: a message giving up after MaxAttempts — or one whose event name was never
    // registered at all — is the single most operationally interesting event the outbox
    // produces, and before this it was silent.
    [LoggerMessage(
        Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventType}) dead-lettered. {Reason}")]
    private static partial void LogDeadLettered(ILogger logger, Guid messageId, string eventType, string reason);
}
