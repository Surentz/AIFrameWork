namespace AiFramework.Infrastructure.Resilience;

/// <summary>
/// The retry budget an operator needs to reach without a deploy, and the kill switch that
/// turns every pipeline into a straight pass-through. Bound from the "Resilience" configuration
/// section in Program.cs — not here; see <see cref="ResilienceRegistration.AddResilience"/> for
/// why the binding lives there.
/// </summary>
/// <remarks>
/// The four timing values map onto the strategies <c>AddStandardResilienceHandler()</c>
/// composes, in the order it composes them: total request timeout, then retry, then the
/// circuit breaker, then the per-attempt timeout. The ordering is the part hand-rolled
/// pipelines get wrong — the total timeout is OUTSIDE the retry, so a retry budget can never
/// outlive the caller's patience, and the attempt timeout is INSIDE it, so one hung socket
/// cannot consume the whole budget. Do not rearrange it. ADR 0014.
/// </remarks>
public sealed class ResilienceOptions
{
    /// <summary>
    /// The kill switch. False makes every resilience pipeline a plain HttpClient call — one
    /// attempt, no retry, no breaker, and no timeout beyond the client's own.
    /// </summary>
    /// <remarks>
    /// Load-bearing under test, for the same reason <c>CacheOptions.Enabled</c> is: a test
    /// asserting that an unreachable provider answers 503 must not first sit through three
    /// backoff delays, and tests/Api.IntegrationTests/ApiFactory.cs bans exactly that kind of
    /// waiting.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The ceiling on one logical call, retries included. This is the value that bounds how
    /// long a caller waits, and the only one that bounds how long a retry inside
    /// HybridCache's shared cache factory stalls every OTHER concurrent caller of that key.
    /// </summary>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The ceiling on a single attempt. Must not exceed <see cref="TotalRequestTimeout"/>:
    /// the standard handler validates that itself and throws while building the pipeline, which
    /// is a startup failure rather than a request failure — precisely the shape Release-only
    /// breakage takes in this repository, so the validation in AddResilience catches it first
    /// with a message that names the property.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Retries after the first attempt, so 3 means up to 4 calls. Zero is valid and means
    /// "timeouts and the breaker, but never retry" — the setting a non-idempotent write
    /// integration uses instead of hoping its provider deduplicates. ADR 0014.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// The first backoff delay; later ones grow exponentially with jitter. A provider's own
    /// Retry-After header wins over this — HttpRetryStrategyOptions.ShouldRetryAfterHeader
    /// defaults to true, and that is deliberately left alone.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Where the reference exchange-rate client points. Configuration rather than a constant so
    /// tests can aim it at a stub: no test in this repository may reach the live provider, and
    /// a base address that is not overridable is the thing that would let one.
    /// </summary>
    /// <remarks>
    /// Key-less on purpose. An API key could not live in appsettings*.json — the no-secrets
    /// rule forbids it and .claude/hooks/no-secrets.ps1 blocks the edit — so the reference
    /// integration had to be one that needs none.
    /// </remarks>
    public string ExchangeRateBaseAddress { get; set; } = "https://api.frankfurter.app";
}
