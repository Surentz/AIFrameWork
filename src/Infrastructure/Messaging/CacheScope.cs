namespace AiFramework.Infrastructure.Messaging;

/// <summary>
/// The single source of cache key and tag composition. Both sides of the pipeline come through
/// here — the caching behavior writes an entry under <see cref="Key"/> and tags it with
/// <see cref="Tag"/>, and the eviction behavior removes by <see cref="Tag"/> — because the tag
/// being the key's own prefix is the whole mechanism. Composing either string anywhere else
/// invites a drift that no test would see: eviction would simply stop matching, with no error.
/// </summary>
internal static class CacheScope
{
    /// <summary>"GetOrders:3f2a...:20:" — the query type, the caller, then the query's own part.</summary>
    internal static string Key(string queryName, Guid userId, string part) =>
        $"{Tag(queryName, userId)}:{part}";

    /// <summary>"GetOrders:3f2a..." — everything one caller has cached for one query type.</summary>
    internal static string Tag(string queryName, Guid userId) =>
        $"{queryName}:{userId}";
}
