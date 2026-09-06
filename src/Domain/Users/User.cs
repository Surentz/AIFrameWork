using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Users;

public sealed class User : Entity
{
    /// <summary>The longest username <see cref="Register"/> accepts, mirrored by the EF mapping.</summary>
    public const int MaxUsernameLength = 32;

    /// <summary>The longest display name <see cref="Register"/> accepts, mirrored by the EF mapping.</summary>
    public const int MaxDisplayNameLength = 64;

    private User(
        Guid id,
        string username,
        string usernameNormalized,
        string passwordHash,
        string displayName,
        DateTimeOffset registeredAt)
    {
        Id = id;
        Username = username;
        UsernameNormalized = usernameNormalized;
        PasswordHash = passwordHash;
        DisplayName = displayName;
        RegisteredAt = registeredAt;
    }

    public Guid Id { get; private set; }

    /// <summary>As the user typed it. Shown back to them; never used to look them up.</summary>
    public string Username { get; private set; }

    /// <summary>
    /// The case-folded form the unique index is on, so that "Ada" and "ada" are one account.
    /// Postgres would otherwise need the citext extension to compare case-insensitively.
    /// </summary>
    public string UsernameNormalized { get; private set; }

    public string PasswordHash { get; private set; }

    public string DisplayName { get; private set; }

    public DateTimeOffset RegisteredAt { get; private set; }

    /// <summary>
    /// The one way a username becomes a lookup key. Callers that search by username must go
    /// through this rather than lower-casing their own way, or a user could register a name
    /// that no sign-in attempt ever matches.
    /// </summary>
    public static string Normalize(string username)
    {
        ArgumentNullException.ThrowIfNull(username);

        return username.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Takes an already-hashed password: hashing needs a dependency, and Domain has none. The
    /// same reason <see cref="Orders.Order.Place"/> is handed a timestamp rather than reading a clock.
    /// </summary>
    public static User Register(
        Guid id, string username, string passwordHash, string displayName, DateTimeOffset registeredAt)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new DomainException("A user needs a username.");
        }

        if (username.Length > MaxUsernameLength)
        {
            throw new DomainException($"A username cannot be longer than {MaxUsernameLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainException("A user needs a password.");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new DomainException("A user needs a display name.");
        }

        if (displayName.Length > MaxDisplayNameLength)
        {
            throw new DomainException(
                $"A display name cannot be longer than {MaxDisplayNameLength} characters.");
        }

        var trimmed = username.Trim();

        return new User(id, trimmed, Normalize(trimmed), passwordHash, displayName.Trim(), registeredAt);
    }

    public void ChangePassword(string newPasswordHash)
    {
        if (string.IsNullOrWhiteSpace(newPasswordHash))
        {
            throw new DomainException("A user needs a password.");
        }

        PasswordHash = newPasswordHash;
    }
}
