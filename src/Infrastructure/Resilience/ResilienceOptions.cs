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
/// cannot consume the whole budget. Do not rearrange it.
/// <para>
/// The rate limiter and circuit breaker are NOT configurable from here — only their four
/// siblings below are. Both keep the standard handler's own defaults (1000 concurrent permits;
/// a circuit breaker that needs 100 samples before it can even evaluate a failure ratio), which
/// no realistic call volume from this application - individually, or under this repository's
/// entire test suite - comes close to tripping. A knob for each was considered and cut: neither
/// has ever needed adjusting, and a knob nobody turns is a knob nobody has verified works.
/// </para>
/// </remarks>
public sealed class ResilienceOptions
{
    /// <summary>
    /// The kill switch — for retrying, specifically. False makes the retry strategy's
    /// <c>ShouldHandle</c> predicate reject every outcome, so it never fires regardless of
    /// <see cref="MaxRetryAttempts"/>: one attempt, no backoff wait, whatever the response.
    /// </summary>
    /// <remarks>
    /// NOT implemented as <c>MaxRetryAttempts = 0</c>, which reads as the obvious approach and
    /// is not available: Polly's own validation on that property requires at least 1, thrown as
    /// an <c>OptionsValidationException</c> from inside the pipeline-build callback — discovered
    /// by this exact attempt failing every test that took this path. Suppressing the predicate
    /// instead leaves <c>MaxRetryAttempts</c> at whatever it is configured to and simply never
    /// consults it.
    /// <para>
    /// This does NOT touch the timeouts, the circuit breaker, or the rate limiter — those exist
    /// to bound worst-case latency and concurrent load, not to slow down a well-behaved caller,
    /// and none of the three has ever tripped inside this repository's own test suite.
    /// "Disabled" therefore means "never retries," not "the pipeline does not exist" — the
    /// resilience handler is attached either way, because Microsoft.Extensions.Http.Resilience
    /// has no supported way to attach it conditionally at the point IServiceCollection
    /// registration runs, before any option is resolvable.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Load-bearing under test, for the same reason <c>CacheOptions.Enabled</c> is: a test
    /// asserting that an unreachable provider answers 503 must not first sit through three
    /// backoff delays, and tests/Api.IntegrationTests/ApiFactory.cs bans exactly that kind of
    /// waiting. Retrying is the only one of the five strategies whose default behavior costs
    /// real wall-clock time against a slow or failing dependency, which is why it is the one
    /// this switch reaches.
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
    /// Retries after the first attempt, so 3 means up to 4 calls.
    /// </summary>
    /// <remarks>
    /// Must be at least 1 — discovered the hard way, not a design choice: Polly's own
    /// <c>RetryStrategyOptions&lt;T&gt;.MaxRetryAttempts</c> validation rejects zero
    /// ("must be between 1 and 2147483647"), thrown from inside the pipeline-build callback the
    /// first time a request is made, not at startup where <c>ValidateOnStart</c> could catch it.
    /// A non-idempotent write integration that must never retry therefore cannot reach for
    /// "MaxRetryAttempts = 0" — see <see cref="Enabled"/>'s own remarks for the mechanism that
    /// actually achieves "never retry" without hitting this floor. ADR 0014.
    /// </remarks>
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
    /// <para>
    /// <c>api.frankfurter.dev</c>, not the older <c>api.frankfurter.app</c> the design docs for
    /// this feature originally named: checked while implementing this, <c>.app</c> carries a
    /// <c>Deprecation</c> header already past its own date and now only answers through a 301 to
    /// <c>.dev</c>. Same <c>/v1/latest?from=..&amp;to=..</c> shape either way, so only the host
    /// changed. <c>.dev</c>'s own successor-version header points at a <c>/v2/rates</c> endpoint
    /// with a different, undocumented parameter shape — not worth chasing for a reference
    /// integration whose test suite never calls the live provider anyway.
    /// </para>
    /// </remarks>
    public string ExchangeRateBaseAddress { get; set; } = "https://api.frankfurter.dev/v1";
}
