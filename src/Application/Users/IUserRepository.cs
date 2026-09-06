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
}
