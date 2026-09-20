using AiFramework.Application.Users;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

/// <summary>
/// One projected, uncached read per authenticated request.
/// <para>
/// The absence of caching is load-bearing, not an oversight. HybridCache here is L1 only and
/// cannot be evicted across pods (ADR 0010), while whoever rotates a stamp - an administrator,
/// or the same user on another device - is, under ingress cookie affinity, almost certainly on a
/// different pod from the session being invalidated. Caching this read would reintroduce exactly
/// the staleness the stamp exists to remove. The cost is a primary-key lookup on a pooled
/// connection; the exit, if it ever measurably matters, is interval-based revalidation in the
/// manner of ASP.NET Core Identity's SecurityStampValidator, which trades immediacy for
/// throughput.
/// </para>
/// </summary>
internal sealed class SessionValidator(AiFrameworkDbContext context) : ISessionValidator
{
    public async Task<SessionAuthority?> ValidateAsync(
        Guid userId, string stamp, CancellationToken cancellationToken)
    {
        // Fail closed on an absent stamp. This is the pre-ADR-0011 cookie case, and treating it
        // as "no opinion" would leave exactly the sessions this feature exists to retire.
        if (string.IsNullOrEmpty(stamp))
        {
            return null;
        }

        // AsNoTracking and a projection: this runs on every request and must not populate the
        // change tracker with a User the request never asked for, which would then be scanned
        // by SaveChanges and by DomainEventsInterceptor on any later write in the same scope.
        // Role rides along on the read the stamp already pays for: same row, same index seek,
        // one more column on the wire. That is the whole reason ADR 0020 puts the role here
        // instead of in the cookie - a per-request role read is free only because this query
        // already exists, and a role that is never in a cookie can never be stale.
        var stored = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp, u.Role })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Ordinal: the stamp is an opaque token compared against a value the caller already
        // holds, so there is nothing to guess and no culture to respect.
        return stored is not null
            && string.Equals(stored.SecurityStamp, stamp, StringComparison.Ordinal)
                ? new SessionAuthority(stored.Role)
                : null;
    }
}
