using AiFramework.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AiFramework.Api.Notifications;

/// <summary>
/// The realtime endpoint a signed-in client connects to for its own notifications.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately has NO methods. Everything a client can do — read the feed, mark things read —
/// already has a REST endpoint that goes through the command/query pipeline and gets validation,
/// logging and caching behavior for free. A hub method would be a second way in that bypasses all
/// of it, so this hub is push-only: the server talks, the client listens.
/// </para>
/// <para>
/// <c>[Authorize]</c> is load-bearing rather than decorative. Without it the connection is
/// anonymous, <see cref="HubCallerContext.UserIdentifier"/> is null, and the user never joins the
/// group that <c>Clients.User(...)</c> targets — the push would silently reach nobody.
/// </para>
/// </remarks>
[Authorize]
public sealed class NotificationHub : Hub
{
    /// <summary>
    /// The client-side method name a push invokes. A constant rather than a literal repeated in
    /// <see cref="SignalRNotificationPush"/> and in the frontend: the two sides have to agree
    /// exactly, and a typo produces a message nobody is listening for, with no error on either
    /// end.
    /// </summary>
    public const string NotificationReceived = "notificationReceived";
}

/// <summary>
/// The SignalR implementation of <see cref="INotificationPush"/>. Lives in Api because
/// <see cref="IHubContext{THub}"/> is an HTTP-transport concern that Application and
/// Infrastructure must not reference — the port is what lets a handler two layers down reach it.
/// </summary>
public sealed partial class SignalRNotificationPush(
    IHubContext<NotificationHub> hub, ILogger<SignalRNotificationPush> logger) : INotificationPush
{
    public async Task PushAsync(
        Guid userId, NotificationListItem notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // Clients.User targets every connection whose UserIdentifier matches - by default the
        // NameIdentifier claim, which AuthController stamps with the user id. That covers the
        // same person on two tabs or two devices without any group bookkeeping here, and with a
        // backplane it covers their connections on OTHER replicas too.
        try
        {
            await hub.Clients.User(userId.ToString())
                .SendAsync(
                    NotificationHub.NotificationReceived,
                    notification,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        // The contract on INotificationPush says an implementation must not throw: this runs on
        // the outbox pump AFTER the notification is committed, so letting a transport failure
        // escape would fail the message and redeliver work that already succeeded. The row is
        // durable and the client picks it up on its next read, so a failed push costs latency,
        // not data. Logged at Warning because a backplane that is down is worth seeing.
        //
        // OperationCanceledException is excluded deliberately: that is the host shutting down,
        // not this push failing, and OutboxWorkItemProcessor relies on it propagating so the
        // row's lease expires and another worker reclaims it.
#pragma warning disable CA1031
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogPushFailed(logger, userId, notification.Id, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Realtime push failed for user {UserId}, notification {NotificationId}. " +
            "It is still in their feed; only the live update was lost.")]
    private static partial void LogPushFailed(
        ILogger logger, Guid userId, Guid notificationId, Exception exception);
}
