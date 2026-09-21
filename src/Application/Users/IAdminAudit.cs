namespace AiFramework.Application.Users;

/// <summary>
/// What one administrator did to one account.
/// </summary>
/// <remarks>
/// Names rather than integers on the wire and in the column, like <see cref="SignInOutcome"/>:
/// stored as an integer, reordering a member would silently change what every existing row means.
/// </remarks>
public enum AdminActionKind
{
    /// <summary>Moved to <see cref="Domain.Users.UserRole.Admin"/>.</summary>
    Promoted,

    /// <summary>Moved off it.</summary>
    Demoted,

    /// <summary>Every session for the target revoked, by rotating their security stamp.</summary>
    SignedOutEverywhere,
}

/// <summary>
/// Records administrative changes for the monitoring page. See ADR 0022.
/// </summary>
/// <remarks>
/// <para>
/// <b>The row is written in the same <c>SaveChangesAsync</c> as the change it describes.</b>
/// That is the opposite of <see cref="ISignInAudit"/>, which writes immediately and outside any
/// unit of work — and the difference is deliberate. A sign-in audit must record FAILURES, which
/// the unit of work would discard along with the failed command; an administrative audit records
/// only changes that happened, so an audit row that could commit without its effect, or an effect
/// that could commit without its row, would be worse than either. Tracked, therefore, and
/// committed by the same behavior that commits the role change.
/// </para>
/// <para>
/// <b>Usernames are denormalized on purpose.</b> The record has to stay readable whatever later
/// happens to either account, and a join that resolves to nothing is not an audit trail.
/// </para>
/// </remarks>
public interface IAdminAudit
{
    /// <summary>
    /// Adds one record of <paramref name="kind"/> performed on <paramref name="target"/>. The
    /// actor is read from the ambient <c>ICurrentUser</c>; there is no administrative action
    /// without one, so a caller cannot forget to say who did it.
    /// </summary>
    public Task RecordAsync(
        AdminActionKind kind, AdministeredUser target, CancellationToken cancellationToken);
}

/// <summary>
/// The account an administrative action was performed on, carried rather than re-read so the
/// audit records the username as it was at the time.
/// </summary>
public sealed record AdministeredUser(Guid Id, string Username);
