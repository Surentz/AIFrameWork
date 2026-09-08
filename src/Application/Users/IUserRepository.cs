using AiFramework.Domain.Users;

namespace AiFramework.Application.Users;

public interface IUserRepository
{
    public Task AddAsync(User user, CancellationToken cancellationToken);

    public Task<User?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Keyed on the normalized form, so lookups must pass <see cref="User.Normalize"/>'s output
    /// rather than the raw username.
    /// </summary>
    public Task<User?> GetByNormalizedUsernameAsync(
        string usernameNormalized, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the sign-in counter immediately, independently of the unit of work.
    /// <para>
    /// This exists because <c>Behaviors.CommitAsync</c> commits only a successful Result, and
    /// sign-in's failure path is the one that must persist. A tracked mutation there would be
    /// discarded without an error, leaving the counter at zero forever and the lockout dead.
    /// </para>
    /// </summary>
    public Task RecordSignInOutcomeAsync(
        Guid userId, int attempts, DateTimeOffset? lockedOutUntil, CancellationToken cancellationToken);
}
