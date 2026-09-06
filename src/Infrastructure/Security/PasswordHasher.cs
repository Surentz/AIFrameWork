using AiFramework.Application.Users;
using Microsoft.AspNetCore.Identity;

namespace AiFramework.Infrastructure.Security;

/// <summary>
/// Microsoft's <see cref="PasswordHasher{TUser}"/>, used on its own. It arrives in
/// Microsoft.Extensions.Identity.Core, which is a plain library - none of the Identity stack
/// (UserManager, SignInManager, the EF stores, the eight tables) is referenced or registered.
///
/// Not hand-rolled: it does PBKDF2 with a versioned format byte, so the iteration count can be
/// raised later without invalidating existing hashes, and it compares in constant time. Those
/// are the parts that are easy to get subtly and silently wrong.
/// </summary>
public sealed class PasswordHasher : IPasswordHasher
{
    // PasswordHasher<TUser> ignores its user argument entirely - it is there for callers who
    // subclass it to vary the work factor per user. `object` rather than the User aggregate so
    // that satisfying the `where TUser : class` constraint needs no null-forgiving operator and
    // no fabricated User that would have to pass its own invariants.
    private static readonly object Unused = new();

    private readonly PasswordHasher<object> _inner = new();

    public string Hash(string password) => _inner.HashPassword(Unused, password);

    public bool Verify(string passwordHash, string password) =>
        // SuccessRehashNeeded means the stored hash used weaker settings than the current
        // defaults. It is still a correct password, so it signs in; upgrading the stored hash on
        // the way through is a deliberate follow-up, not part of this slice.
        _inner.VerifyHashedPassword(Unused, passwordHash, password)
            is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
}
