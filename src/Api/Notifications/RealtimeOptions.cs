namespace AiFramework.Api.Notifications;

/// <summary>
/// Whether realtime notification push is on, and what backs it across replicas.
/// </summary>
/// <remarks>
/// Bound from the "Realtime" section in <c>Program.cs</c>, the same way <c>CacheOptions</c> is
/// and for the same reason — see <c>src/Infrastructure/CLAUDE.md</c>.
///
/// Config keys use double underscores in the environment, like every other section here:
/// <c>Realtime__Enabled</c>, <c>Realtime__RedisConnectionString</c>. A single underscore binds
/// nothing and warns nothing.
/// </remarks>
public sealed class RealtimeOptions
{
    /// <summary>
    /// OFF by default, matching <c>Observability:Otlp:Enabled</c>'s reasoning: the default must
    /// be the one that works with nothing else running, so a developer who has started no Redis,
    /// and every CI job, still gets a green build. The REST feed is unaffected either way — push
    /// is an optimization over it, never the source of truth.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The backplane's connection string, in StackExchange.Redis format
    /// (<c>host:port</c>, plus options). Empty means NO backplane.
    /// </summary>
    /// <remarks>
    /// Leaving this empty while <see cref="Enabled"/> is true is legitimate ONLY at a single
    /// replica — a developer's <c>dotnet run</c>, or the compose stack. Above one replica it is
    /// the silent-failure configuration this whole option exists to avoid, which is why
    /// <c>Program.cs</c> logs a warning rather than letting it pass unremarked. See ADR 0019.
    /// </remarks>
    public string RedisConnectionString { get; set; } = string.Empty;
}
