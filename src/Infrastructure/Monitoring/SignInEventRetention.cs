using AiFramework.Application.Abstractions;
using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// Deletes sign-in events past retention with one set-based statement, like
/// <see cref="JobRunRetention"/> and for the same reasons — with one more: this table is personal
/// data, so the sweep is a requirement rather than tidiness.
/// </summary>
internal sealed class SignInEventRetention(
    AiFrameworkDbContext context, IOptions<MonitoringOptions> options, IClock clock)
    : ISignInEventRetention
{
    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-options.Value.SignInEventRetentionDays);

        return context.SignInEvents
            .Where(e => e.At < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
