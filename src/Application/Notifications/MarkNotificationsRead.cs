using AiFramework.Application.Abstractions;
using FluentValidation;

namespace AiFramework.Application.Notifications;

/// <summary>
/// What both mark-read commands answer with.
/// </summary>
/// <param name="MarkedCount">
/// How many this call actually changed — 0 or 1 for <see cref="MarkNotificationRead"/>, any
/// number for <see cref="MarkAllNotificationsRead"/>. Zero is a success, not a failure: re-reading
/// something already read is a client being imprecise, and <see cref="Domain.DomainException"/>
/// is explicitly not for that (see <see cref="Domain.Notifications.Notification.MarkRead"/>).
/// </param>
/// <param name="UnreadCount">
/// The badge count after the change, so a client updating its bell icon needs no second
/// round-trip. Read inside the same request as the write, but BEFORE the unit-of-work behavior
/// commits — see the handlers for why that is still the right number.
/// </param>
public sealed record NotificationReadResult(int MarkedCount, int UnreadCount);

/// <summary>Marks one notification read. Idempotent: calling it twice is not an error.</summary>
public sealed record MarkNotificationRead(Guid Id) : ICommand<NotificationReadResult>;

public sealed class MarkNotificationReadValidator : AbstractValidator<MarkNotificationRead>
{
    public MarkNotificationReadValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public sealed class MarkNotificationReadHandler(
    INotificationRepository notifications, ICurrentUser currentUser, IClock clock)
    : ICommandHandler<MarkNotificationRead, NotificationReadResult>
{
    public async Task<Result<NotificationReadResult>> HandleAsync(
        MarkNotificationRead command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<NotificationReadResult>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        // Scoped by signature: one belonging to another user comes back null, exactly like one
        // that does not exist, so this cannot confirm an id is real to someone who does not own it.
        var notification = await notifications
            .GetForUpdateAsync(command.Id, userId, cancellationToken)
            .ConfigureAwait(false);

        if (notification is null)
        {
            return Result.Failure<NotificationReadResult>(new Error(
                ErrorKind.NotFound, "notifications.not_found", "That notification does not exist."));
        }

        var changed = notification.MarkRead(clock.UtcNow);

        var storedUnread = await notifications
            .CountUnreadAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        // CountUnreadAsync runs as SQL against the last COMMITTED state, and the unit-of-work
        // behavior has not committed this change yet — EF does not flush pending changes before
        // a scalar query — so the row just marked is still counted. Subtracting what this call
        // changed is what makes the returned number the one the caller will see on their next
        // read. Forcing a SaveChanges here to avoid the arithmetic is not an option: handlers
        // never commit (that is the behavior's job, exactly once) and doing so would turn one
        // command into two transactions.
        //
        // Advisory under concurrency, in BOTH directions: another request marking a different
        // notification read between the count and the commit makes this one high, and a
        // concurrent mark-all-read committing in that same window leaves storedUnread at 0 while
        // this call still changed something — which without the clamp reports -1. A badge is
        // never negative; the caller's next read corrects either drift.
        var unread = Math.Max(0, storedUnread - (changed ? 1 : 0));

        return Result.Success(new NotificationReadResult(changed ? 1 : 0, unread));
    }
}

/// <summary>
/// Marks every unread notification read.
/// </summary>
/// <remarks>
/// No validator: the command carries no input to validate, and an empty
/// <c>AbstractValidator</c> would be a registration and a pipeline pass that assert nothing.
/// Absence is tolerated at dispatch time by design — see
/// <c>RegistrationCompletenessTests</c>, which requires every validator that EXISTS to be
/// registered, not that every command has one.
/// </remarks>
public sealed record MarkAllNotificationsRead : ICommand<NotificationReadResult>;

public sealed class MarkAllNotificationsReadHandler(
    INotificationRepository notifications, ICurrentUser currentUser, IClock clock)
    : ICommandHandler<MarkAllNotificationsRead, NotificationReadResult>
{
    public async Task<Result<NotificationReadResult>> HandleAsync(
        MarkAllNotificationsRead command, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<NotificationReadResult>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        var unread = await notifications
            .ListUnreadForUpdateAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        var readAt = clock.UtcNow;

        // One timestamp for the whole batch rather than one per row: they were all read by the
        // same gesture, and re-reading the clock per iteration would spread them across an
        // interval that never happened.
        var marked = unread.Count(n => n.MarkRead(readAt));

        // Everything unread was just marked, so the remaining count is zero by construction.
        // Not re-queried: the rows are tracked-but-uncommitted, and asking the database again
        // would either repeat the work or — worse — answer from a provider that has not seen
        // the change, which is the kind of disagreement this returns a number to avoid.
        return Result.Success(new NotificationReadResult(marked, 0));
    }
}
