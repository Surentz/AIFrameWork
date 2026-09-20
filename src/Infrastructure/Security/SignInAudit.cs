using System.Diagnostics;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Users;
using AiFramework.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiFramework.Infrastructure.Security;

/// <summary>
/// One attempt to authenticate, as the monitoring page reads it. See ADR 0021.
/// </summary>
/// <remarks>
/// <b>This table is personal data.</b> It holds an IP address and a user-agent against a
/// username, which is what makes it worth having and also what makes its retention sweep part of
/// the feature rather than housekeeping — see <c>PruneSignInEvents</c>.
/// </remarks>
public sealed class SignInEvent
{
    public required Guid Id { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>Null for <see cref="SignInOutcome.UnknownUser"/> — there is no account to point at.</summary>
    public Guid? UserId { get; init; }

    /// <summary>What was typed. The only record of an attempt against a name that does not exist.</summary>
    public required string UsernameAttempted { get; init; }

    public required SignInOutcome Outcome { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public string? TraceId { get; init; }
}

/// <summary>
/// Writes sign-in events with immediate SQL, never through the change tracker.
/// </summary>
/// <remarks>
/// <para>
/// Three of the five outcomes are failures, and <c>Behaviors.CommitAsync</c> commits only a
/// successful <c>Result</c> — so a tracked write would be silently discarded on exactly the
/// attempts most worth recording. The same constraint that already forces
/// <c>TryRecordFailedSignInAsync</c> to bypass the unit of work, and the same mechanism
/// <c>OrderAuditWriter</c> uses.
/// </para>
/// <para>
/// The outcome is written as its NAME, matching the EF mapping: as an integer, reordering a
/// member would silently change what every existing row means.
/// </para>
/// </remarks>
internal sealed class SignInAudit(
    AiFrameworkDbContext context, IClock clock, IClientContext client) : ISignInAudit
{
    /// <summary>
    /// Long enough for an IPv6 address with a scope id, and for a proxy chain's first hop. A
    /// user-agent is capped far shorter than the header may legally be: the header is caller
    /// controlled, and an unbounded column fed by one is a place to put a megabyte.
    /// </summary>
    private const int MaxIpAddressLength = 64;

    private const int MaxUserAgentLength = 512;

    public Task RecordAsync(
        SignInOutcome outcome,
        string usernameAttempted,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usernameAttempted);

        var at = clock.UtcNow;
        var name = outcome.ToString();
        var username = Truncate(usernameAttempted, Domain.Users.User.MaxUsernameLength);
        var ip = Truncate(client.IpAddress, MaxIpAddressLength);
        var agent = Truncate(client.UserAgent, MaxUserAgentLength);
        var traceId = Activity.Current?.TraceId.ToString();

        return context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO sign_in_events
                ("Id", "At", "UserId", "UsernameAttempted", "Outcome", "IpAddress", "UserAgent", "TraceId")
            VALUES
                ({Guid.NewGuid()}, {at}, {userId}, {username}, {name}, {ip}, {agent}, {traceId})
            """,
            cancellationToken);
    }

    /// <summary>
    /// Truncated here rather than left to the database, which would throw and turn a monitoring
    /// concern into a failed sign-in. Every one of these values is caller-controlled.
    /// </summary>
    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
