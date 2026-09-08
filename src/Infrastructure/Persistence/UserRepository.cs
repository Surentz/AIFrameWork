using AiFramework.Application.Users;
using AiFramework.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Persistence;

public sealed class UserRepository(AiFrameworkDbContext context) : IUserRepository
{
    public async Task AddAsync(User user, CancellationToken cancellationToken) =>
        await context.Users.AddAsync(user, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Tracked, unlike the reads in <see cref="OrderRepository"/>: ChangePassword mutates the
    /// user it loads here, and a no-tracking read would leave the new hash unsaved with no error.
    /// </summary>
    public Task<User?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        context.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <summary>No-tracking: sign-in and the availability check both only read.</summary>
    public Task<User?> GetByNormalizedUsernameAsync(
        string usernameNormalized, CancellationToken cancellationToken) =>
        context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UsernameNormalized == usernameNormalized, cancellationToken);

    /// <summary>
    /// ExecuteUpdateAsync, not a tracked mutation: it issues its own UPDATE straight away, so it
    /// does not depend on the unit of work committing. See the port's documentation.
    /// </summary>
    public Task RecordSignInOutcomeAsync(
        Guid userId, int attempts, DateTimeOffset? lockedOutUntil, CancellationToken cancellationToken) =>
        context.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.FailedSignInAttempts, attempts)
                    .SetProperty(u => u.LockedOutUntil, lockedOutUntil),
                cancellationToken);
}
