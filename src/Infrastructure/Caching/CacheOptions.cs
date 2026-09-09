namespace AiFramework.Infrastructure.Caching;

/// <summary>
/// The two things an operator needs to reach without a deploy. Per-query durations are NOT here:
/// they are literals on the query itself, because the query is what knows how stale its own
/// result may acceptably be, and a record in Application has nothing to inject configuration
/// through. Bound from the "Cache" configuration section in Program.cs.
/// </summary>
public sealed class CacheOptions
{
    /// <summary>The kill switch. False makes every cached query a straight handler call.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A ceiling every ICacheable.Duration is clamped to, so a careless literal in Application
    /// cannot retain a page for an hour. A cap, not a default — it never raises a duration.
    /// </summary>
    public TimeSpan MaximumDuration { get; set; } = TimeSpan.FromMinutes(1);
}
