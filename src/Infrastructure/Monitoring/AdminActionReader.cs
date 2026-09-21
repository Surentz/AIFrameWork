using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// Reads <c>admin_actions</c>, no-tracking and projected — this only ever displays.
/// </summary>
internal sealed class AdminActionReader(AiFrameworkDbContext context) : IAdminActionReader
{
    public async Task<AdminActionPage> ListAsync(
        Guid? targetUserId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = context.AdminActions.AsNoTracking();

        if (targetUserId is { } id)
        {
            // Served by IX_AdminActions_TargetUserId_At_Desc, which is what makes one account's
            // history a seek rather than a scan of the whole table.
            query = query.Where(a => a.TargetUserId == id);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(a => a.At)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AdminActionView(
                a.Id,
                a.At,
                a.Kind,
                a.ActorUserId,
                a.ActorUsername,
                a.TargetUserId,
                a.TargetUsername,
                a.IpAddress,
                a.TraceId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AdminActionPage(items, total, page);
    }
}
