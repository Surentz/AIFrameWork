using AiFramework.Domain.Users;

namespace AiFramework.Api.Monitoring;

/// <summary>
/// Who is looking at the monitoring page. Carries no operational data yet — phases 2 to 4 add
/// endpoints for that beside this one.
/// </summary>
public sealed record MonitoringAccessResponse
{
    public required Guid UserId { get; init; }

    public required string Username { get; init; }

    /// <summary>
    /// Always <see cref="UserRole.Admin"/> on a successful response — the policy admits nobody
    /// else. Returned anyway so the page can render who it is showing, and so a future read-only
    /// operator role has somewhere to appear without a contract change.
    /// </summary>
    public required UserRole Role { get; init; }
}
