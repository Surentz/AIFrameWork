using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>The status publisher's writer: one call replaces the table with the current picture.</summary>
internal interface IExternalSystemStatusStore
{
    public Task ReplaceAsync(IReadOnlyCollection<ExternalSystemStatusView> current, CancellationToken cancellationToken);
}

/// <summary>
/// <c>external_system_status</c>'s writer and reader. The write is an upsert per system plus a
/// delete of every other row: two worker replicas publish the same picture, so last write wins
/// and nothing can collide, and a system removed from configuration disappears rather than
/// lingering as its last status.
/// </summary>
internal sealed class ExternalSystemStatusStore(AiFrameworkDbContext context)
    : IExternalSystemStatusStore, IExternalSystemStatusReader
{
    public const int MaxDescriptionLength = 512;

    public async Task ReplaceAsync(
        IReadOnlyCollection<ExternalSystemStatusView> current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        foreach (var row in current)
        {
            var state = row.State.ToString();
            var description = row.Description is { Length: > MaxDescriptionLength }
                ? row.Description[..MaxDescriptionLength]
                : row.Description;

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO external_system_status
                    ("Name", "State", "Description", "CheckedAt", "CertificateNotAfter", "TokenOk")
                VALUES
                    ({row.Name}, {state}, {description}, {row.CheckedAt}, {row.CertificateNotAfter}, {row.TokenOk})
                ON CONFLICT ("Name") DO UPDATE SET
                    "State" = EXCLUDED."State",
                    "Description" = EXCLUDED."Description",
                    "CheckedAt" = EXCLUDED."CheckedAt",
                    "CertificateNotAfter" = EXCLUDED."CertificateNotAfter",
                    "TokenOk" = EXCLUDED."TokenOk"
                """,
                cancellationToken).ConfigureAwait(false);
        }

        var names = current.Select(row => row.Name).ToList();
        await context.ExternalSystemStatuses
            .Where(row => !names.Contains(row.Name))
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
