using AiFramework.Application.Abstractions;

namespace AiFramework.Application.Notifications;

/// <summary>
/// The number on the bell icon. Its own query rather than a field on
/// <see cref="GetNotifications"/> because it is polled far more often than the feed is read, and
/// answering it must not materialize a single row — see
/// <see cref="INotificationRepository.CountUnreadAsync"/>.
///
/// Not <c>ICacheable</c>, for the reasons on <see cref="GetNotifications"/>.
/// </summary>
public sealed record GetUnreadNotificationCount : IQuery<int>;

public sealed class GetUnreadNotificationCountHandler(
    INotificationRepository notifications, ICurrentUser currentUser)
    : IQueryHandler<GetUnreadNotificationCount, int>
{
    public async Task<Result<int>> HandleAsync(
        GetUnreadNotificationCount query, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return Result.Failure<int>(new Error(
                ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
        }

        var count = await notifications
            .CountUnreadAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(count);
    }
}
