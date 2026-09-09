namespace AiFramework.Application.Abstractions;

/// <summary>
/// A query whose successful result may be cached. Opt-in: a query without this interface is
/// never cached, which is why nothing on the auth path has it.
/// </summary>
/// <remarks>
/// <see cref="CacheKey"/> is ONLY the part that varies with this query's own arguments. The
/// caching behavior prepends the query type name and the calling user's id, so a query cannot
/// omit the user scope — a key shared across users would serve one caller another's data, which
/// is a leak rather than a stale read. Do not put a user id in CacheKey; it is already there.
/// </remarks>
public interface ICacheable
{
    public string CacheKey { get; }

    /// <summary>
    /// How long a successful result may be served from cache. Clamped to
    /// CacheOptions.MaximumDuration, so this can only ever be shortened by configuration.
    /// </summary>
    public TimeSpan Duration { get; }
}

/// <summary>
/// A command that invalidates cached queries belonging to the caller who issued it. Eviction
/// runs synchronously, after the command's transaction commits and before the response returns.
/// </summary>
/// <remarks>
/// Tags name query TYPES — use <c>nameof(GetOrders)</c>, not a hand-written string. The calling
/// user's id is prepended by the behavior, exactly as it is for keys, so one caller's write never
/// evicts another's entries.
/// <para>
/// Synchronous rather than riding the domain-event path, and read-your-own-writes is the reason:
/// the frontend refetches milliseconds after a 201, and must not be served a page that omits
/// what it just created. The outbox is polled, so an event-driven eviction would land too late.
/// </para>
/// </remarks>
public interface IInvalidatesCache
{
    public IReadOnlyList<string> Tags { get; }
}
