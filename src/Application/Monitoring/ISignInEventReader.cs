using AiFramework.Application.Users;

namespace AiFramework.Application.Monitoring;

/// <summary>
/// Reads what <see cref="ISignInAudit"/> wrote, plus the two account-state questions that belong
/// beside it. A separate port from the audit for the same reason <c>IJobRunReader</c> is separate
/// from the recorder: opposite directions, opposite hosts.
/// </summary>
public interface ISignInEventReader
{
    public Task<SignInEventPage> ListAsync(
        SignInOutcome? outcome,
        string? username,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Accounts whose lockout has not yet expired, soonest to expire first.</summary>
    public Task<IReadOnlyList<LockedOutUserView>> ListLockedOutAsync(
        DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>How many users were last seen at or after <paramref name="since"/>.</summary>
    public Task<int> CountActiveAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Counts by outcome for attempts at or after <paramref name="since"/>.</summary>
    public Task<IReadOnlyDictionary<SignInOutcome, int>> CountByOutcomeAsync(
        DateTimeOffset since, CancellationToken cancellationToken);
}
