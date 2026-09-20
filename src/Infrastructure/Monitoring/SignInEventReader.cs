using AiFramework.Application.Monitoring;
using AiFramework.Application.Users;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Monitoring;

/// <summary>
/// Reads the sign-in audit and the two account-state questions beside it. Every read is
/// <c>AsNoTracking</c> and projected: none is followed by a write.
/// </summary>
internal sealed class SignInEventReader(AiFrameworkDbContext context) : ISignInEventReader
{
    public async Task<SignInEventPage> ListAsync(
        SignInOutcome? outcome,
        string? username,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = context.SignInEvents.AsNoTracking();

        if (outcome is { } wanted)
        {
            query = query.Where(e => e.Outcome == wanted);
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            // Case-insensitive on the ATTEMPTED name rather than joining to users: an attempt
            // against an account that does not exist has no user to join to, and those are
            // exactly the rows an operator investigating an enumeration sweep is looking for.
            var normalized = username.Trim();
            query = query.Where(e => EF.Functions.ILike(e.UsernameAttempted, $"%{normalized}%"));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new SignInEventView(
                e.Id, e.At, e.UserId, e.UsernameAttempted, e.Outcome, e.IpAddress, e.UserAgent, e.TraceId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SignInEventPage(items, total, page);
    }

    public async Task<IReadOnlyList<LockedOutUserView>> ListLockedOutAsync(
        DateTimeOffset now, CancellationToken cancellationToken) =>
        await context.Users
            .AsNoTracking()
            .Where(u => u.LockedOutUntil != null && u.LockedOutUntil > now)
            .OrderBy(u => u.LockedOutUntil)
            .Select(u => new LockedOutUserView(u.Id, u.Username, u.LockedOutUntil!.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<int> CountActiveAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        context.Users
            .AsNoTracking()
            .CountAsync(u => u.LastSeenAt != null && u.LastSeenAt >= since, cancellationToken);

    public async Task<IReadOnlyDictionary<SignInOutcome, int>> CountByOutcomeAsync(
        DateTimeOffset since, CancellationToken cancellationToken)
    {
        var counts = await context.SignInEvents
            .AsNoTracking()
            .Where(e => e.At >= since)
            .GroupBy(e => e.Outcome)
            .Select(group => new { Outcome = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return counts.ToDictionary(entry => entry.Outcome, entry => entry.Count);
    }
}
