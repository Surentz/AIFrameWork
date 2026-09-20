using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;

namespace AiFramework.Application.Monitoring;

/// <summary>Sign-in counts, who is locked out, and who is about — the logins overview in one read.</summary>
public sealed record SignInHealthView(
    int Succeeded,
    int BadCredentials,
    int LockedOut,
    int UnknownUser,
    DateTimeOffset Since,
    ActiveUsersView Active,
    IReadOnlyList<LockedOutUserView> LockedOutUsers);

public sealed record GetSignInHealth(TimeSpan Window, TimeSpan ActiveWindow)
    : IQuery<SignInHealthView>;

public sealed class GetSignInHealthHandler(ISignInEventReader events, IClock clock)
    : IQueryHandler<GetSignInHealth, SignInHealthView>
{
    public async Task<Result<SignInHealthView>> HandleAsync(
        GetSignInHealth query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Window <= TimeSpan.Zero || query.Window > GetJobHealthHandler.MaxWindow)
        {
            return Result.Failure<SignInHealthView>(new Error(
                ErrorKind.Validation,
                "monitoring.window_invalid",
                $"The window must be positive and no more than {GetJobHealthHandler.MaxWindow.TotalDays} days."));
        }

        if (query.ActiveWindow <= TimeSpan.Zero || query.ActiveWindow > GetJobHealthHandler.MaxWindow)
        {
            return Result.Failure<SignInHealthView>(new Error(
                ErrorKind.Validation,
                "monitoring.window_invalid",
                "The active window must be positive and no more than the maximum window."));
        }

        var now = clock.UtcNow;
        var since = now - query.Window;

        var counts = await events.CountByOutcomeAsync(since, cancellationToken).ConfigureAwait(false);

        // Locked-out accounts are read as of NOW rather than over the window: a lockout that has
        // already expired is not something an operator can act on, and ADR 0008's window is fixed
        // rather than sliding, so "locked" is only ever a question about the present.
        var lockedOut = await events.ListLockedOutAsync(now, cancellationToken).ConfigureAwait(false);

        var active = await events
            .CountActiveAsync(now - query.ActiveWindow, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(new SignInHealthView(
            Succeeded: counts.GetValueOrDefault(SignInOutcome.Succeeded),
            BadCredentials: counts.GetValueOrDefault(SignInOutcome.BadCredentials),
            LockedOut: counts.GetValueOrDefault(SignInOutcome.LockedOut),
            UnknownUser: counts.GetValueOrDefault(SignInOutcome.UnknownUser),
            Since: since,
            Active: new ActiveUsersView(active, query.ActiveWindow),
            LockedOutUsers: lockedOut));
    }
}
