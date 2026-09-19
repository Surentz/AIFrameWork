namespace AiFramework.Application.Notifications;

/// <summary>
/// Delivers a notification to a connected client the moment it is written, instead of waiting
/// for that client's next poll. A transport, not a store: the feed in the database is the truth,
/// and this is an optimization on top of it.
/// </summary>
/// <remarks>
/// <para>
/// Injected as <c>IEnumerable&lt;INotificationPush&gt;</c>, never as a bare
/// <c>INotificationPush</c>, and that is deliberate. The implementation lives in the Api layer
/// (it needs SignalR's hub context, which is an HTTP-transport concern Application and
/// Infrastructure must not reference), while the handlers that call it are constructed from
/// <c>AddInfrastructure</c> alone — including by
/// <c>RegistrationCompletenessTests.AddInfrastructure_ResolvesAHandlerForEveryRegisteredDomainEvent</c>,
/// which forces every handler chain to actually construct. A required dependency registered only
/// in <c>Program.cs</c> would make that test fail for a reason unrelated to what it guards.
/// </para>
/// <para>
/// An empty sequence is therefore a legitimate configuration meaning "no realtime transport is
/// wired up", not a missing registration: clients fall back to polling the REST feed, which is
/// what they do on a dropped connection anyway. That is why this is an enumerable rather than a
/// no-op default implementation — there is no fake to mistake for a real one.
/// </para>
/// <para>
/// **Push is best-effort, permanently.** A client that is disconnected, or connected to a replica
/// that never hears about the write, simply misses it and catches up on its next read. No caller
/// may treat a successful push as delivery, and no caller may skip writing the row because it
/// pushed.
/// </para>
/// </remarks>
public interface INotificationPush
{
    /// <summary>
    /// Delivers one notification to one user, on whatever connections they currently have.
    /// </summary>
    /// <remarks>
    /// Implementations must not throw: this runs on the outbox pump, after the notification is
    /// already committed, so a transport failure must not fail the message and cause a redelivery
    /// of work that already succeeded.
    /// </remarks>
    public Task PushAsync(
        Guid userId, NotificationListItem notification, CancellationToken cancellationToken);
}
