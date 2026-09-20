using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Jobs;

/// <summary>
/// One attempt at one job, as the monitoring page reads it. Keyed on
/// (<see cref="EnvelopeId"/>, <see cref="Attempt"/>) rather than on a surrogate: Wolverine's
/// message id is stable across retries, so the pair is what makes a redelivery idempotent AND
/// keeps each attempt its own row. See ADR 0021.
/// </summary>
public sealed class JobRun
{
    public required Guid EnvelopeId { get; init; }

    public required int Attempt { get; init; }

    public required string JobName { get; init; }

    /// <summary>Null only for a job absent from <c>JobRegistration.Jobs</c>.</summary>
    public JobLane? Lane { get; init; }

    public required JobRunStatus Status { get; set; }

    public required DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public long? DurationMs { get; set; }

    public Guid? OwnerId { get; init; }

    public string? Error { get; set; }

    public string? TraceId { get; init; }

    public string? InstanceId { get; init; }
}

/// <summary>
/// Writes job-run rows with immediate SQL, never through the change tracker.
/// </summary>
/// <remarks>
/// <para>
/// <c>ExecuteSqlInterpolatedAsync</c>, following <c>OrderAuditWriter</c>'s precedent and for the
/// same class of reason. The run being recorded is frequently one whose handler is throwing, so
/// its transaction is rolling back around this call — a tracked mutation would go with it, and the
/// failure nobody can see is the one worth recording most. Writing through the tracker would also
/// mean <c>SaveChangesAsync</c> from inside middleware, which would commit the handler's
/// half-finished work as a side effect.
/// </para>
/// <para>
/// Table and column names are quoted exactly as EF's default PascalCase convention generates them.
/// An unquoted identifier is folded to lower case by Postgres and silently fails to match — the
/// hazard <c>OrderAuditWriter</c> and <c>OutboxPoller</c> both record.
/// </para>
/// <para>
/// Enums are written as their NAMES, matching the EF mapping. Stored as integers, reordering a
/// member would silently change what every existing row means.
/// </para>
/// </remarks>
internal sealed class JobRunRecorder(AiFrameworkDbContext context, IClock clock) : IJobRunRecorder
{
    public Task StartedAsync(JobRunAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        var startedAt = clock.UtcNow;
        var lane = attempt.Lane?.ToString();
        var status = nameof(JobRunStatus.Running);

        // ON CONFLICT DO UPDATE rather than DO NOTHING: the same (envelope, attempt) pair arrives
        // again when a worker died mid-handler and the lease was reclaimed. That is a genuine
        // re-run of that attempt, so the row is reset to Running rather than left showing the
        // stale outcome of a run that no longer exists.
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO job_runs
                ("EnvelopeId", "Attempt", "JobName", "Lane", "Status", "StartedAt",
                 "OwnerId", "TraceId", "InstanceId")
            VALUES
                ({attempt.EnvelopeId}, {attempt.Attempt}, {attempt.JobName}, {lane}, {status},
                 {startedAt}, {attempt.OwnerId}, {attempt.TraceId}, {attempt.InstanceId})
            ON CONFLICT ("EnvelopeId", "Attempt") DO UPDATE SET
                "Status" = EXCLUDED."Status",
                "StartedAt" = EXCLUDED."StartedAt",
                "CompletedAt" = NULL,
                "DurationMs" = NULL,
                "Error" = NULL
            """,
            cancellationToken);
    }

    public Task SucceededAsync(Guid envelopeId, int attempt, CancellationToken cancellationToken) =>
        CompleteAsync(envelopeId, attempt, nameof(JobRunStatus.Succeeded), error: null, cancellationToken);

    public Task FailedAsync(
        Guid envelopeId, int attempt, string error, CancellationToken cancellationToken) =>
        CompleteAsync(envelopeId, attempt, nameof(JobRunStatus.Failed), error, cancellationToken);

    /// <summary>
    /// Duration is computed in SQL from the row's own <c>StartedAt</c> rather than passed in, so
    /// the middleware carries no per-message state between its Before and its After — which is
    /// what lets the middleware stay static, as Wolverine's conventions require.
    /// </summary>
    private Task<int> CompleteAsync(
        Guid envelopeId, int attempt, string status, string? error, CancellationToken cancellationToken)
    {
        var completedAt = clock.UtcNow;

        return context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE job_runs SET
                "Status" = {status},
                "CompletedAt" = {completedAt},
                "DurationMs" = GREATEST(
                    0,
                    (EXTRACT(EPOCH FROM ({completedAt}::timestamptz - "StartedAt")) * 1000)::bigint),
                "Error" = {error}
            WHERE "EnvelopeId" = {envelopeId} AND "Attempt" = {attempt}
            """,
            cancellationToken);
    }
}
