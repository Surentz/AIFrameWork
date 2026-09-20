using System.Security.Cryptography;
using AiFramework.Domain.Abstractions;

namespace AiFramework.Domain.Users;

public sealed class User : Entity
{
    /// <summary>The longest username <see cref="Register"/> accepts, mirrored by the EF mapping.</summary>
    public const int MaxUsernameLength = 32;

    /// <summary>The longest display name <see cref="Register"/> accepts, mirrored by the EF mapping.</summary>
    public const int MaxDisplayNameLength = 64;

    /// <summary>
    /// Consecutive failed sign-ins that trigger a lockout. A business rule about what counts as
    /// an attack, so it lives here rather than in configuration.
    /// </summary>
    public const int MaxFailedSignInAttempts = 5;

    /// <summary>How long a lockout lasts. Fixed, never sliding — see RegisterFailedSignIn.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The widest stamp the EF mapping allows. 128 bits hex-encoded is 32 characters; the slack is
    /// for a future format change and costs nothing in Postgres.
    /// </summary>
    public const int MaxSecurityStampLength = 64;

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
        Role = UserRole.Member;
        SecurityStamp = NewStamp();
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
    /// What this user may do beyond their own data. Every user registers as
    /// <see cref="UserRole.Member"/>; only <see cref="ChangeRole"/> moves it, and only the
    /// administrator reconciler calls that, driven from configuration. See ADR 0020.
    /// </summary>
    public UserRole Role { get; private set; }

    /// <summary>Consecutive failures since the last successful sign-in.</summary>
    public int FailedSignInAttempts { get; private set; }

    /// <summary>When the current lockout expires, or null when the account is not locked.</summary>
    public DateTimeOffset? LockedOutUntil { get; private set; }

    /// <summary>
    /// Opaque nonce identifying the current authority of this user's sessions. Written into the
    /// session cookie at sign-in and compared on every authenticated request, so rotating it
    /// invalidates every cookie already issued for this user. Rotated by anything that changes what
    /// a session is allowed to do: a password change, a lockout, an explicit sign-out-everywhere,
    /// and (from plan 2) a permission change. See ADR 0011.
    /// </summary>
    public string SecurityStamp { get; private set; }

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

        // Ends every session issued under the old password. The session doing the changing is
        // re-issued by AuthController, so the person changing their own password stays signed in
        // while anyone else holding a cookie for this account does not. See ADR 0011.
        RotateSecurityStamp();
    }

    /// <summary>
    /// Grants or revokes administrative access. Returns true when the role actually moved, so a
    /// caller reconciling a whole list can tell an effective change from a no-op and write nothing
    /// when the configuration has not changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This deliberately does NOT rotate the security stamp, unlike every other change to what a
    /// session is allowed to do. The role is not carried in the cookie: it is read from this row
    /// on every authenticated request, alongside the stamp — see <c>SessionValidator</c> — so
    /// there is no already-issued credential holding stale authority for a rotation to invalidate.
    /// Rotating here would sign the user out of a session they remain perfectly entitled to hold,
    /// and a startup reconciler that rotated would log every administrator out on every restart.
    /// </para>
    /// <para>
    /// ADR 0011 anticipated the opposite — its summary lists "(from plan 2) a permission change"
    /// as a rotation trigger. ADR 0020 supersedes that: the list stays at three.
    /// </para>
    /// </remarks>
    public bool ChangeRole(UserRole role)
    {
        if (Role == role)
        {
            return false;
        }

        Role = role;

        return true;
    }

    /// <summary>
    /// Invalidates every session already issued for this user. The single primitive behind
    /// "sign out everywhere", lockout enforcement, and a password change. Those three, and the
    /// list is closed.
    /// </summary>
    /// <remarks>
    /// A role change is deliberately NOT on that list, though ADR 0011's own summary anticipated
    /// it would be. Nothing issued carries the role, so there is nothing for a rotation to
    /// invalidate. See <see cref="ChangeRole"/> and ADR 0020.
    /// </remarks>
    public void RotateSecurityStamp() => SecurityStamp = NewStamp();

    public bool IsLockedOut(DateTimeOffset now) => LockedOutUntil is { } until && until > now;

    /// <summary>
    /// Records a failed attempt, locking the account on the threshold failure. Handed the time
    /// rather than reading a clock, the same reason <see cref="Orders.Order.Place"/> is handed
    /// a timestamp.
    /// </summary>
    public void RegisterFailedSignIn(DateTimeOffset now)
    {
        // The window is fixed, never sliding: an attempt made while a lockout is live must not
        // count and must not push the expiry forward, or an attacker who knows one username could
        // hold that account locked indefinitely at one guess per window. SignInHandler refuses
        // such an attempt before ever reaching here, but the invariant belongs to the type that
        // owns the two fields, not to its one caller.
        if (IsLockedOut(now))
        {
            return;
        }

        // Serving out a lockout earns a fresh set of attempts. Without this reset the count
        // would still stand at MaxFailedSignInAttempts when the window lapsed, so the very next
        // failure would reach the threshold again and re-lock — and every failure after it would
        // too. That is a sliding window arriving through the back door: it would let an attacker
        // hold an account locked indefinitely at one guess per window.
        //
        // A stamp still set here has necessarily lapsed: the guard above returned if it had not.
        if (LockedOutUntil is not null)
        {
            FailedSignInAttempts = 0;
            LockedOutUntil = null;
        }

        FailedSignInAttempts++;

        if (FailedSignInAttempts >= MaxFailedSignInAttempts)
        {
            LockedOutUntil = now + LockoutDuration;

            // Locking the account has to cut off whoever is already inside it. Without this, an
            // attacker holding a cookie from an earlier successful sign-in is untouched by the lockout
            // - the gap ADR 0011 records against ADR 0008. Only on the failure that actually locks:
            // rotating on every wrong guess would let anyone who knows a username sign that user out
            // at will.
            RotateSecurityStamp();
        }
    }

    public void RegisterSuccessfulSignIn()
    {
        FailedSignInAttempts = 0;
        LockedOutUntil = null;
    }

    // Generated here rather than handed in the way Id is. The stamp is opaque - no test asserts a
    // specific value, only that it changed - so injecting it would buy no determinism while
    // changing three handler signatures. Deliberate departure from Order.Place's convention.
    //
    // 128 bits from the platform CSPRNG, hex-encoded - the same construction ASP.NET Core Identity
    // uses for its own security stamp. A Guid is not specified to be unpredictable; this is a value
    // an attacker must not be able to guess or influence, not merely one that must be unique.
    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
