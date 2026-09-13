# Backend resilience: retry policies for outbound calls and for the database

**Date:** 2026-09-13
**Status:** Approved, not yet implemented

## Context

This codebase has no synchronous retry anywhere. That statement needs qualifying, because it
has three *asynchronous* retry mechanisms already and the design below must not duplicate any
of them:

- `OutboxWorkItemProcessor.FailAsync` — exponential backoff with 20% jitter, capped at
  `MaxBackoff`, `MaxAttempts = 5`, then dead-letter. The event path is solved.
- `SignInHandler.RecordFailureAsync` — one concurrency retry, deliberately not a loop, because
  "a second lost race means the counter is moving anyway".
- Wolverine's own error policies on the ADR 0005 event path.

What none of those covers is the request path: a query that hits Postgres while a pod is
restarting, or a call to a third party that answers 503. Today both surface to the caller as an
unhandled exception and a 500.

**There is no outbound HTTP in this repository at all.** No `HttpClient`, no
`IHttpClientFactory`, no `AddHttpClient` anywhere under `src/`; the only matches are test
clients. `src/Infrastructure/CLAUDE.md` already lists "HTTP clients for external services" as
belonging to that layer, but none has ever been written. So this is not a retrofit — it is the
first one, and what it establishes is the shape every later integration copies.

**Database access has no retry either.** `InfrastructureRegistration.cs` registers
`options.UseNpgsql(connectionString)` with nothing after it. Under `docker compose` that is
invisible, because Postgres never moves. Under the kind cluster of ADR 0010 it is not: two API
replicas outlive a Postgres pod restart, and every in-flight request during that window fails.

The framing matters, because it decides what "done" means. As with ADR 0009, there is no slow
endpoint to point at and no incident to cite. The deliverable is a correct, hard-to-misuse
shape — one that makes the *unsafe* retry awkward to write — not a latency number.

### The thing that is easy to get wrong here

Retry is the one reliability pattern that can make an outage worse. Three specific ways, all of
which this repository is currently arranged to walk into:

1. **Retrying a non-idempotent write.** A POST to a payment provider that times out may well
   have succeeded. Retrying it charges twice. Nothing in a resilience pipeline knows this;
   only the integration's author does.
2. **Retrying without a deadline.** `Behaviors.CachedAsync` runs a query handler inside
   `HybridCache.GetOrCreateAsync`, whose factory work is *shared across concurrent callers*.
   A retry budget in there does not stall one request, it stalls every concurrent caller of
   that key, for the whole budget.
3. **Retry amplification.** Three retries at each of three layers is twenty-seven calls to a
   service that is already failing. This is why the design below puts retry at exactly one
   layer per call path and says so explicitly.

## Decisions

### 1. `Microsoft.Extensions.Http.Resilience`, referenced from Infrastructure only

Version 10.10.0, which sits on the same `Microsoft.Extensions.*` 10.x train as the
`Microsoft.Extensions.Caching.Hybrid` 10.9.0 already in
`AiFramework.Infrastructure.csproj`. It brings Polly v8 (`Polly.Core`) transitively.

`AddStandardResilienceHandler()` composes five strategies in a fixed, correct order:

| # | Strategy | Default |
|---|---|---|
| 1 | Rate limiter (concurrency) | 1000 permits, no queue |
| 2 | **Total request timeout** | 30s |
| 3 | Retry | 3 attempts, 2s base, exponential + jitter |
| 4 | Circuit breaker | 10% failure ratio over 30s, 100 min throughput, 5s break |
| 5 | Attempt timeout | 10s |

That ordering is the part hand-rolled pipelines get wrong. The total timeout is *outside* the
retry, so the retry budget cannot outlive the caller's patience; the attempt timeout is
*inside* it, so one hung socket does not consume the whole budget. Getting this backwards is
how a "resilient" client becomes the reason a thread pool starves.

The retry predicate handles `HttpRequestException`, `TimeoutRejectedException`, 5xx, 408 and
429, and — because `HttpRetryStrategyOptions.ShouldRetryAfterHeader` defaults to true — honours
a `Retry-After` header over its own backoff. It does **not** retry other 4xx. Both halves of
that are correct and neither should be widened.

**The package must never be referenced from Application.** This is the same rule
`Application/CLAUDE.md` already states for caching ("No caching package may be referenced from
this layer — that is the point of the markers"), for the same reason: the port describes what
the use case needs, and the layer that owns the transport owns its failure policy.

Raw `Polly` plus a hand-written `DelegatingHandler` was the alternative. Rejected: it is more
code, it re-derives the ordering above by hand, and it gives up the metrics and `Activity` tags
the standard handler emits for free.

### 2. The pipeline sits *under* the port, never over it

This is a constraint discovered from `Result<T>`, not a preference.

Polly decides whether to retry by inspecting an outcome — an exception, or an
`HttpResponseMessage`. A port that returns `Result<T>` has already converted the failure into
a *successful* return value by the time Polly sees it. Wrapping a `Result`-returning call in a
resilience pipeline therefore produces a pipeline that never retries anything, silently, while
every test that asserts "the pipeline is registered" stays green.

So the arrangement is fixed:

```
Application:     IExchangeRateProvider -> Result<ExchangeRate>      (no Polly, no HttpClient)
Infrastructure:  ExchangeRateClient    -> HttpResponseMessage       (Polly lives here)
                   ^ AddHttpClient<...>().AddStandardResilienceHandler()
```

The adapter converts only the **final** outcome to `Result<T>`. The rule that falls out of
this, and which belongs in `src/Infrastructure/CLAUDE.md` rather than in a comment on one
class: **never return a failed `Result` from inside a Polly-wrapped delegate.** Throw, or
return the raw response, and convert at the adapter's boundary.

### 3. Retry only what is safe to retry, and make the unsafe case loud

`AddStandardResilienceHandler` retries by HTTP status, not by verb. It will happily retry a
POST. For a GET against a read-only endpoint that is exactly right; for a POST that creates
something at the far end it is a duplicate side effect.

The reference client established here is **read-only by construction**, which is the honest way
to establish a pattern without also designing an idempotency-key scheme nobody needs yet. The
rule for the first write integration is recorded in ADR 0014 rather than left to be discovered:

> A non-GET third-party call must either carry a caller-generated idempotency key that the
> provider honours, or disable retry on that client. There is no third option, and
> "the provider is probably idempotent" is not one of them.

### 4. The reference integration is exchange rates, and it is small on purpose

`IExchangeRateProvider` in Application, `ExchangeRateClient` in Infrastructure, backed by
`https://api.frankfurter.app` — a free, public, **key-less** rates API. Key-less matters twice:
the no-secrets rule forbids an API key in `appsettings*.json`, and `.claude/hooks/no-secrets.ps1`
would block the edit anyway.

It is exposed through one `GetExchangeRate` query and one `GET /api/rates` action. That is
five small files, and the reason for a real consumer rather than a registered-but-uncalled
client is that an uncalled client is dead code in a repository whose build treats warnings as
errors and whose culture treats untested registrations as untrue.

The choice of *this* integration is not arbitrary. `/api/rates` is the only place in the
repository where four things meet at once:

- a retry pipeline,
- `HybridCache`'s shared factory (the query is `ICacheable`),
- a total request timeout,
- and the new `ErrorKind.Unavailable`.

Those four interacting is precisely the thing a later author needs an example of. A contrived
client with no consumer would demonstrate none of it.

`[Authorize]`, for the same mechanical reason `ProductsController` is: an `ICacheable` query
dispatched with no caller throws by design.

### 5. `EnableRetryOnFailure`, with a budget that fits inside the readiness probe

```csharp
options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(
    maxRetryCount: 3,
    maxRetryDelay: TimeSpan.FromSeconds(1),
    errorCodesToAdd: null));
```

One line, and it covers every repository read, every `SaveChangesAsync` the unit-of-work
behavior issues, and every `ExecuteUpdateAsync`. Four consequences, each of which is a real
behaviour change in this repository rather than a generic caveat:

**(a) Explicit transactions now have to go through the strategy.** With an execution strategy
configured, `BeginTransactionAsync` throws `InvalidOperationException` unless wrapped in
`CreateExecutionStrategy().ExecuteAsync(...)` — because the strategy cannot retry a block it
does not own. `src/` contains no `BeginTransactionAsync` today (the only one is
`OrderAuditWriterTests.cs:72`, in a test, against its own context). So this costs nothing now
and becomes a standing rule in `Infrastructure/CLAUDE.md`. It is much cheaper to adopt before
the first explicit transaction exists than after.

**(b) `OutboxPoller.ClaimAsync` gets nothing from it, deliberately.** It builds a raw
`NpgsqlCommand` on `context.Database.GetDbConnection()` and executes it directly, bypassing
EF's execution strategy entirely. Leaving it alone is the right call, not an oversight: a
failed claim is already recovered twice over — the next poll cycle re-runs it, and
`ClaimAsync`'s own `LeasedUntil` clause reclaims any row a dead worker was holding. Adding a
retry there would buy a second of latency on a path that is already self-healing. A comment
saying so goes above the raw command, because the next reader will otherwise assume it was
missed.

**(c) The sign-in failure counter can now double-count, and that is the safe direction.**
`RecordSignInOutcomeAsync` is a single `ExecuteUpdateAsync`. A single UPDATE is atomic, so a
retry either applies the increment or does not. The ambiguous case is narrow but real: the
statement commits and the acknowledgement is lost, the strategy retries, and the counter
advances twice for one failed sign-in. For a lockout counter, over-counting locks an account
slightly early and under-counting would let guessing continue — ADR 0008 would rather have the
former. Written down here so it is a decision rather than a surprise.

**(d) The readiness probe gets slower when the database is genuinely down.** `AddDbContextCheck`
calls `CanConnectAsync`, which goes through the strategy like everything else. `k8s/base/api.yaml`
sets no `timeoutSeconds` on the readiness probe, so Kubernetes' default of **1 second** applies
and the probe would fail by timeout rather than by a clean 503.

That is why `maxRetryDelay` is 1 second rather than EF's 30-second default. Note the floor is
not the retry delay but Npgsql's connection timeout: against a host that blackholes SYN rather
than refusing, a single attempt can sit for the connection string's `Timeout` (15s by default)
and three attempts for three times that. The mitigation is to set `Timeout=5` on the deployed
connection string and an explicit `timeoutSeconds: 5` on the probe, both of which this plan
does. A readiness probe that measures "is Postgres reachable *after* three retries" is
measuring the wrong thing.

### 6. `ErrorKind.Unavailable`, mapped to 503 with `Retry-After`

`ErrorKind` today is `Validation | NotFound | Conflict | Unauthorized`, and
`ResultExtensions.Problem` maps anything else to 500. "The third party is down and we tried"
is not a 500 — the request was well-formed and the server is fine.

A fifth member, mapped to `503 Service Unavailable`, with a `Retry-After` header. The header is
the point: the repository already answers 429 with `Retry-After` from the rate limiter's
`OnRejected`, and a 503 without one leaves a client doing exactly the guessing that
`AuthRateLimitTests` exists to prevent. `ResultExtensions.Problem` gains its first response
*header* alongside the body it already builds.

This is an API contract change, so `openapi/AiFramework.Api.json` and
`frontend/src/api/schema.d.ts` are regenerated by the documented command. The frontend needs no
other change: `client.ts`'s `ApiError` already carries an arbitrary `status`, so a 503 surfaces
as an `ApiError` with `status === 503` and the provider's detail message with no new branch.

### 7. One `ResilienceOptions`, bound in `Program.cs`, off under test

Exactly the `CacheOptions` shape, including the part that is easy to get wrong:
`AddResilience()` registers and **validates** the options but does not bind configuration,
because binding there would make `IOptions<ResilienceOptions>` require an `IConfiguration` that
a bare `ServiceCollection` in a unit test does not have. `Program.cs` binds the `"Resilience"`
section, the same way it already binds `"Cache"` and reads `Wolverine:Durable`.

`Enabled = false` makes every pipeline a straight pass-through. That switch is load-bearing
under test for the same reason `Cache:Enabled=false` is: a test asserting that an unreachable
provider produces a 503 must not first sit through three backoff delays, and
`ApiFactory.cs:204` bans exactly that kind of waiting — "no `Thread.Sleep`, no `Task.Delay`, no
retry-until-timeout".

### 8. Backoff is tested against a fake clock, and `IClock` is not touched

Polly v8 takes a `TimeProvider` on `ResiliencePipelineBuilder`, so the backoff schedule is
assertable with `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 —
no real delay, no flake, no violation of the rule above.

`IClock` stays exactly as it is. It is the Application port for *domain* time, deliberately
minimal (`UtcNow` and nothing else), and `TimeProvider` is an Infrastructure detail that Polly
happens to require. Widening `IClock` into a `TimeProvider`, or registering `SystemClock` as
one, would push a scheduling abstraction into a layer that has no business with scheduling, to
save one registration line.

No test hits `api.frankfurter.app`. Infrastructure tests stub `HttpMessageHandler`; the base
address is configuration, and `ApiFactory` points it at a stub.

## Accepted risks

**Circuit-breaker state is per-process.** Two replicas mean two independent breakers, so one
pod can be open while the other is closed and still sending traffic. This is the same shape as
the L1-only `HybridCache` constraint ADR 0010 records, and the same answer applies: at two
replicas it is a rounding error, and the alternative is shared state nobody wants yet.

**A 503 from `/api/rates` is a legitimate steady state.** If the provider is unreachable from
CI's network, the endpoint answers 503 correctly. The e2e suite must therefore not assert a
200 from `/api/rates` against the live provider — it asserts the *stubbed* behaviour, or it
leaves the endpoint alone.

**Retry hides a slow dependency rather than reporting it.** Three attempts at 2s turn a
one-second failure into a seven-second success, and the caller sees latency instead of an
error. The total request timeout bounds it; the metrics the standard handler emits are what
make it visible. This is the trade retry always makes and it is not avoidable, only bounded.

## Out of scope

- **Write integrations and idempotency keys.** Recorded as a rule in ADR 0014, not built.
- **A retry behavior in the dispatch pipeline.** Replaying a whole command re-runs side effects
  that are not idempotent, and the outbox already owns at-least-once delivery for the things
  that are. Rejected rather than deferred.
- **Retry inside `OutboxPoller`**, per decision 5(b).
- **Distributed circuit-breaker state**, per the accepted risk above.
- **Frontend retry.** TanStack Query has its own, and a client retrying a 503 that already
  carries `Retry-After` is a separate decision with its own failure modes.
