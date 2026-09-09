namespace AiFramework.Infrastructure.Caching;

internal static class CacheDuration
{
    /// <summary>
    /// Caps a query's requested duration at the configured ceiling. Extracted from the caching
    /// behavior so the only arithmetic in the TTL is directly testable: HybridCache expires on
    /// its own internal clock, so no test can observe an entry actually lapsing.
    /// </summary>
    internal static TimeSpan Clamp(TimeSpan requested, TimeSpan maximum) =>
        requested < maximum ? requested : maximum;
}
