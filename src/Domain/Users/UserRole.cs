namespace AiFramework.Domain.Users;

/// <summary>
/// What a user may do beyond their own data.
/// </summary>
/// <remarks>
/// <para>
/// Two members on purpose. There is one privileged surface — the monitoring page — and no second
/// axis of privilege in evidence, so the smallest thing that expresses the requirement is an enum
/// rather than a grant model of named permissions. The exit, if a read-only operator or
/// per-resource administration is ever wanted, is for this to become one input to an
/// authorization requirement rather than the whole of it; the policy name at the call sites would
/// not change. See ADR 0020.
/// </para>
/// <para>
/// Persisted as a STRING, never as its underlying integer — see <c>UserConfiguration</c>. Stored
/// as an int, adding a member or reordering the ones that exist silently changes what every
/// existing row means, with nothing to catch it. Same reasoning that makes
/// <c>NotificationKind</c> and <c>OrderStatus</c> cross the wire as names.
/// </para>
/// </remarks>
public enum UserRole
{
    /// <summary>
    /// The default, and what every existing row reads as. Sees only their own data — which is
    /// every endpoint in this application except the monitoring page.
    /// </summary>
    Member,

    /// <summary>Reads the monitoring page and performs its operator actions.</summary>
    Admin,
}
