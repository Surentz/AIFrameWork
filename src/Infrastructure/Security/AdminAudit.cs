using System.Diagnostics;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using AiFramework.Infrastructure.Persistence;

namespace AiFramework.Infrastructure.Security;

/// <summary>
/// One administrative change, as the monitoring page reads it. See ADR 0022.
/// </summary>
/// <remarks>
/// <b>This table is personal data, like <c>sign_in_events</c></b> — it holds an address and a
/// user-agent against two usernames — so its retention sweep is part of the feature rather than
/// housekeeping. See <c>PruneAdminActions</c>.
/// </remarks>
public sealed class AdminAction
{
    public required Guid Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required AdminActionKind Kind { get; init; }

    public required Guid ActorUserId { get; init; }

    /// <summary>
    /// Denormalized, deliberately: the record must stay readable whatever later happens to the
    /// acting account, and a join that resolves to nothing is not an audit trail.
    /// </summary>
    public required string ActorUsername { get; init; }

    public required Guid TargetUserId { get; init; }

    /// <summary>Denormalized for the same reason as <see cref="ActorUsername"/>.</summary>
    public required string TargetUsername { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    /// <summary>Pivots to the request's own logs, exactly as <c>SignInEvent.TraceId</c> does.</summary>
    public string? TraceId { get; init; }
}

/// <summary>
/// Adds audit rows to the change tracker, so they commit with the change they describe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tracked, unlike <c>SignInAudit</c>, and the difference is the whole point.</b> That one
/// writes immediate SQL because it must record FAILURES, which the unit of work discards along
/// with the failed command. This one records only changes that happened, so it wants the opposite
/// guarantee: the row and its effect commit together or neither does. <c>Behaviors.CommitAsync</c>
/// runs once after a successful command and saves both.
/// </para>
/// <para>
/// One exception is documented rather than hidden: a demotion is written by
/// <c>IUserRepository.TryDemoteAsync</c> as an immediate conditional UPDATE, because its
/// last-administrator rail cannot be a read-then-write. A commit that then fails leaves the
/// demotion applied and unaudited. That asymmetry is deliberate and is argued on the port.
/// </para>
/// </remarks>
internal sealed class AdminAudit(
    AiFrameworkDbContext context, IClock clock, IClientContext client, ICurrentUser currentUser)
    : IAdminAudit
{
    private const int MaxIpAddressLength = 64;

    private const int MaxUserAgentLength = 512;

    public async Task RecordAsync(
        AdminActionKind kind, AdministeredUser target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        // There is no administrative action without an administrator: every caller reaches this
        // through a policy that already required one, so a missing id is a wiring fault rather
        // than an anonymous request, and recording Guid.Empty would quietly corrupt the trail.
        var actorId = currentUser.Id
            ?? throw new InvalidOperationException(
                "An administrative action was recorded with no current user. "
                + "IAdminAudit is only reachable from endpoints behind the Monitoring policy.");

        var actor = await context.Users
            .FindAsync([actorId], cancellationToken)
            .ConfigureAwait(false);

        await context.AdminActions
            .AddAsync(
                new AdminAction
                {
                    Id = Guid.NewGuid(),
                    At = clock.UtcNow,
                    Kind = kind,
                    ActorUserId = actorId,
                    // The actor's row is already tracked by the request that loaded it in almost
                    // every case, so this is a change-tracker hit rather than a query. The
                    // fallback is the id: an audit row naming an unresolvable actor is still far
                    // better than no audit row.
                    ActorUsername = actor?.Username ?? actorId.ToString(),
                    TargetUserId = target.Id,
                    TargetUsername = target.Username,
                    IpAddress = Truncate(client.IpAddress, MaxIpAddressLength),
                    UserAgent = Truncate(client.UserAgent, MaxUserAgentLength),
                    TraceId = Activity.Current?.TraceId.ToString(),
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Truncated here rather than left to the database, which would throw and turn an audit
    /// concern into a failed administrative action. Both values are caller-controlled.
    /// </summary>
    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
