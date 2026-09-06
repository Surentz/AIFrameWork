namespace AiFramework.Application.Users;

/// <summary>
/// Hashing, as a dependency. A port rather than a direct call so that Domain never sees a
/// plaintext password and Application never names a crypto library.
/// </summary>
public interface IPasswordHasher
{
    public string Hash(string password);

    /// <summary>
    /// Must compare in constant time: a byte-by-byte comparison that returns early leaks how much
    /// of a guess was right.
    /// </summary>
    public bool Verify(string passwordHash, string password);
}
