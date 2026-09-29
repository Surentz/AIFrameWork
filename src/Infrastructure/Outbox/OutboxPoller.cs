using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AiFramework.Infrastructure.Outbox;

/// <summary>
/// A claimed row on its way to a worker. A record struct, never a tracked entity. TraceParent and
/// OccurredAt default so every existing positional construction (tests included) keeps compiling
/// unchanged; ClaimAsync below is the only caller that passes real values. OccurredAt is the
/// row's own timestamp, handed on to handlers as DomainEventContext.OccurredAt - an integration
/// event's occurredAt must be when the aggregate changed, not when a (re)delivery ran.
/// </summary>
public readonly record struct OutboxWorkItem(
    Guid Id,
    string EventName,
    string Payload,
    int Attempt,
    string? TraceParent = null,
    DateTimeOffset OccurredAt = default);

/// <summary>
/// Claims batches of due outbox rows. Exposed as plain async methods rather than a loop so it
/// can be tested without a host — the BackgroundService that calls it stays thin.
/// </summary>
public sealed class OutboxPoller(
    AiFrameworkDbContext context, IOptions<OutboxOptions> options, IClock clock)
{
    private readonly OutboxOptions _options = options.Value;

    /// <summary>
    /// Atomically claims up to BatchSize due rows. FOR UPDATE SKIP LOCKED is what makes this
    /// safe across application instances; the leased_until clause is what recovers rows from a
    /// worker that died mid-handler. Attempts increments HERE, at claim time — if it only
    /// incremented on failure, a message that hard-crashes the process would loop forever.
    /// </summary>
    /// <remarks>
    /// This method gets NOTHING from AddInfrastructure's EnableRetryOnFailure, deliberately: it
    /// builds a raw NpgsqlCommand directly on <c>context.Database.GetDbConnection()</c>, which
    /// bypasses EF's execution strategy entirely rather than merely forgetting to opt in. That
    /// is the right call, not a gap to close, because a failed claim is already recovered twice
    /// over without it — the next poll cycle re-runs it, and the LeasedUntil clause above
    /// reclaims any row a dead worker was holding. Wrapping this in the execution strategy would
    /// buy at most one second of latency on a path that heals itself either way. ADR 0014.
    /// </remarks>
    public async Task<IReadOnlyList<OutboxWorkItem>> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var until = now.Add(_options.LeaseDuration);

        const string Sql = """
            UPDATE outbox SET "Status" = 'InFlight', "LeasedUntil" = @until, "Attempts" = "Attempts" + 1
            WHERE "Id" IN (
                SELECT "Id" FROM outbox
                WHERE ("Status" = 'Pending'  AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= @now))
                   OR ("Status" = 'InFlight' AND "LeasedUntil" < @now)
                ORDER BY "OccurredAt"
                LIMIT @batch
                FOR UPDATE SKIP LOCKED
            )
            RETURNING "Id", "EventName", "Payload", "Attempts", "TraceParent", "OccurredAt";
            """;

        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("until", until);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batch", _options.BatchSize);

        var claimed = new List<OutboxWorkItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claimed.Add(new OutboxWorkItem(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return claimed;
    }

    /// <summary>Deletes Processed rows past retention. Dead rows are never pruned — they are the signal.</summary>
    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.Subtract(_options.RetentionPeriod);

        return context.Outbox
            .Where(m => m.Status == OutboxStatus.Processed
                && m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
