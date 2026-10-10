using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>The status publisher's writer: one call replaces the table with the current picture.</summary>
internal interface IExternalSystemStatusStore
{
    public Task ReplaceAsync(
        IReadOnlyCollection<ExternalSystemStatusView> current,
        IReadOnlyCollection<string> configured,
        CancellationToken cancellationToken);
}

/// <summary>
/// <c>external_system_status</c>'s writer and reader. The write is an upsert per system plus a
/// delete of every row whose system is not CONFIGURED (not merely absent from this report: a
/// report can be empty or partial without the system being gone). Two worker replicas publish the
/// same picture, so last write wins and nothing can collide, and a system removed from
/// configuration disappears rather than lingering as its last status.
/// </summary>
internal sealed class ExternalSystemStatusStore(AiFrameworkDbContext context)
    : IExternalSystemStatusStore, IExternalSystemStatusReader
{
    public const int MaxDescriptionLength = 512;

    public async Task ReplaceAsync(
        IReadOnlyCollection<ExternalSystemStatusView> current,
        IReadOnlyCollection<string> configured,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(configured);

        foreach (var row in current)
        {
            var state = row.State.ToString();
            var description = row.Description is { Length: > MaxDescriptionLength }
                ? row.Description[..MaxDescriptionLength]
                : row.Description;

            // Npgsql writes only offset-0 values to timestamptz; a certificate's NotAfter often is not.
            var checkedAt = row.CheckedAt.ToUniversalTime();
            var certificateNotAfter = row.CertificateNotAfter?.ToUniversalTime();

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO external_system_status
                    ("Name", "State", "Description", "CheckedAt", "CertificateNotAfter", "TokenOk")
                VALUES
                    ({row.Name}, {state}, {description}, {checkedAt}, {certificateNotAfter},{row.TokenOk})
                ON CONFLICT ("Name") DO UPDATE SET
                    "State" = EXCLUDED."State",
                    "Description" = EXCLUDED."Description",
                    "CheckedAt" = EXCLUDED."CheckedAt",
                    "CertificateNotAfter" = EXCLUDED."CertificateNotAfter",
                    "TokenOk" = EXCLUDED."TokenOk"
                """,
                cancellationToken).ConfigureAwait(false);
        }

        // Compared in memory: the names are a handful, and EF cannot translate an invariant ToLower.
        var known = new HashSet<string>(configured, StringComparer.OrdinalIgnoreCase);
        var stored = await context.ExternalSystemStatuses
            .AsNoTracking()
            .Select(row => row.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var unconfigured = stored.Where(name => !known.Contains(name)).ToList();
        await context.ExternalSystemStatuses
            .Where(row => unconfigured.Contains(row.Name))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExternalSystemStatusView>> ListAsync(CancellationToken cancellationToken) =>
        await context.ExternalSystemStatuses
            .AsNoTracking()
            .OrderBy(row => row.Name)
            .Select(row => new ExternalSystemStatusView(
                row.Name, row.State, row.Description, row.CheckedAt, row.CertificateNotAfter, row.TokenOk))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
