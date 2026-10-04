using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Persistence;

/// <summary>
/// Deletes exports past retention with one set-based statement, like <c>SignInEventRetention</c>.
/// By request time, so a request that never finished goes too.
/// </summary>
public sealed class OrderExportRetention(
    AiFrameworkDbContext context, IOptions<OrderExportOptions> options, IClock clock)
    : IOrderExportRetention
{
    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var cutoff = clock.UtcNow.AddDays(-options.Value.RetentionDays);

        return context.OrderExports
            .Where(e => e.RequestedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
