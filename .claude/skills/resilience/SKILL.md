---
name: resilience
description: Use when adding or changing an outbound HTTP client, retry or timeout policy, or an explicit database transaction - Polly and Result<T>, EnableRetryOnFailure and the execution strategy, and Resilience:Enabled under test.
---

# Resilience

`Microsoft.Extensions.Http.Resilience` on outbound HTTP clients (`ExchangeRateClient` is the
reference); `EnableRetryOnFailure` on the Npgsql provider for everything else. Both retry
transient faults automatically — a Postgres pod restarting, a third party answering 503 — so
neither should be reached for again by hand.

**Calling a partner system** — anything with a certificate, a token, or a name in
`ExternalSystems:Systems` — is the `external-systems` skill's job, not a hand-written
`AddHttpClient`: `AddClient<TApi>` attaches this same standard handler with per-system options,
fits the circuit breaker's sampling duration to long attempt timeouts, and adds the certificate,
the token and traffic counting around it. Everything below still applies to its adapters.

Three things that will cost you time:

- **Polly cannot see a failed `Result<T>`.** It decides whether to retry by inspecting an
  exception or an `HttpResponseMessage`; a port that has already converted a failure into
  `Result.Failure(...)` reads to Polly as an ordinary, successful return value, so a pipeline
  wrapped around a `Result`-returning call retries nothing — silently, with every registration
  test staying green. The pipeline must sit *under* the port (inside the Infrastructure adapter,
  on the raw transport outcome), never wrapped around it. See `src/Infrastructure/CLAUDE.md`.
- **An explicit transaction now needs the execution strategy.** `EnableRetryOnFailure` makes a
  bare `context.Database.BeginTransactionAsync()` throw — the strategy cannot retry a block it
  does not own. Use `context.Database.CreateExecutionStrategy().ExecuteAsync(...)` instead.
  Found by `WolverineOutboxAtomicityTests` going red the moment this landed: Wolverine's own EF
  Core outbox integration opens a transaction internally, and needed the same wrapping.
- **Retry is off under test the way the cache is.** `ApiFactory` sets `Resilience:Enabled=false`,
  for the same reason `Cache:Enabled=false` is set: a test asserting an `Unavailable` failure
  must not first sit through the pipeline's own backoff delays. Unlike the cache switch, this one
  does not remove the resilience handler — `AddStandardResilienceHandler` has no supported hook
  to attach conditionally at registration time, before any option is resolvable — it makes the
  retry strategy reject every outcome instead, leaving the timeouts, circuit breaker, and rate
  limiter at their configured values (none of the three has ever tripped in this repo's own
  tests, so none needed neutralising).

See ADR 0014.

