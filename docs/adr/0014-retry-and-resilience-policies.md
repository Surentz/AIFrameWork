# 0014. Retry and resilience policies on the request path

**Date:** 2026-09-13
**Status:** Approved, not yet implemented

## Context

This repository already retries in three places, and none of them is on the request path.
`OutboxWorkItemProcessor` does exponential backoff with jitter and dead-letters at five
attempts. `SignInHandler` retries once when it loses a concurrency race. Wolverine has its own
error policies on the ADR 0005 event path. All three are asynchronous: nobody is waiting.

A request is different. When `GetOrdersHandler` hits Postgres during a pod restart, or a third
party answers 503, there is a caller holding a connection open, and today both surface as an
unhandled exception and a 500.

Two facts about the current code shape the decision more than any preference:

**There is no outbound HTTP in this repository at all.** No `HttpClient`, no
`IHttpClientFactory`, no `AddHttpClient` under `src/`. `Infrastructure/CLAUDE.md` has listed
"HTTP clients for external services" as belonging to that layer since the scaffold, and none
has ever been written. So this decision is not about fixing an integration — it is about what
the first one looks like, and about the fact that every later one will copy it.

**`Result<T>` and Polly disagree about what a failure is.** Polly decides whether to retry by
inspecting an outcome: an exception, or an `HttpResponseMessage`. A port that returns
`Result<T>` has already turned the failure into a successful return value. A resilience
pipeline wrapped around a `Result`-returning call therefore retries nothing, silently, while
every registration test stays green. This is the single sharpest edge in the whole design and
it is invisible at the call site.

## Decision

**`Microsoft.Extensions.Http.Resilience` in Infrastructure only, with the pipeline attached
under the Application port; `EnableRetryOnFailure` on Npgsql with a budget sized to the
readiness probe; and a fifth `ErrorKind` so an exhausted retry answers 503 rather than 500.**

Four parts, each load-bearing:

**The pipeline sits under the port, never over it.** Application declares
`IExchangeRateProvider` returning `Result<T>`. Infrastructure's adapter holds the `HttpClient`,
`AddStandardResilienceHandler()` decorates it, and only the *final* outcome is converted to a
`Result`. The rule that falls out — **nothing inside a Polly-wrapped delegate may return a
failed `Result`** — goes in `Infrastructure/CLAUDE.md`, not in a comment on one class, because
it applies to every integration that will ever be written here.

**The standard handler's strategy order is not rearranged.** Rate limiter, then total request
timeout, then retry, then circuit breaker, then attempt timeout. The total timeout is outside
the retry so the budget cannot outlive the caller's patience; the attempt timeout is inside it
so one hung socket does not consume the whole budget. Hand-rolled pipelines get this backwards,
which is the main reason for taking the packaged one over raw Polly and a `DelegatingHandler`.

**Retry is enabled at exactly one layer per call path.** Three retries at each of three layers
is twenty-seven calls to a service that is already failing. The database retries in EF and
nowhere else; a third party retries in its typed client and nowhere else. There is deliberately
no retry behavior in the dispatch pipeline alongside validate/commit/evict, because replaying
a whole command replays side effects that are not idempotent — and for the ones that are, the
outbox already owns at-least-once delivery.

**A non-GET third-party call must carry an idempotency key or disable retry.** There is no
third option. `AddStandardResilienceHandler` retries by status code, not by verb, so it will
happily replay a POST that already succeeded at the far end. The reference integration
established with this decision is read-only by construction, which is why this is written down
as a rule rather than solved as code: the first payment or email integration is where it bites,
and "the provider is probably idempotent" is not an answer.

## Consequences

**Explicit transactions now have to go through the execution strategy.** With
`EnableRetryOnFailure` configured, a bare `BeginTransactionAsync` throws — the strategy cannot
retry a block it does not own. There are none in `src/` today, which is exactly why this is the
cheap moment to adopt it. It becomes a standing rule under `Infrastructure/CLAUDE.md`'s EF rules.

**`OutboxPoller.ClaimAsync` gets nothing from EF's retry, and that is deliberate.** It builds a
raw `NpgsqlCommand` on the bare connection and bypasses the execution strategy by construction.
A failed claim is already recovered twice — the next poll cycle re-runs it, and the
`LeasedUntil` clause reclaims rows from a dead worker. A comment above the command says so, so
the next reader does not "fix" it.

**The sign-in failure counter can double-count.** `RecordSignInOutcomeAsync` is one
`ExecuteUpdateAsync`; if it commits and the acknowledgement is lost, the strategy retries and
the counter advances twice for one failed attempt. Over-counting locks an account slightly
early; under-counting would let guessing continue. ADR 0008 prefers the former, so this is
accepted rather than worked around.

**The readiness probe measures something slightly different now.** `AddDbContextCheck` calls
`CanConnectAsync`, which goes through the strategy like everything else, so `/health/ready`
answers more slowly when Postgres is genuinely down. `k8s/base/api.yaml` sets no
`timeoutSeconds`, so the Kubernetes default of 1 second applies today and the probe would fail
by timeout rather than by a clean 503. Hence `maxRetryDelay` of 1 second rather than EF's
30-second default, an explicit `timeoutSeconds: 5` on the probe, and `Timeout=5` on the deployed
connection string — because the real floor is Npgsql's 15-second connection timeout against a
host that blackholes SYN, not the retry delay.

**Circuit-breaker state is per-process.** Two replicas means two independent breakers: one pod
can be open while the other still sends traffic. Same shape as the L1-only `HybridCache`
constraint in ADR 0010, same answer — at two replicas it is a rounding error, and shared
breaker state is a dependency nobody wants yet.

**A retry inside the cache factory stalls every concurrent caller of that key.**
`Behaviors.CachedAsync` runs the handler inside `HybridCache.GetOrCreateAsync`, whose factory
work is shared. The total request timeout is what bounds the damage, which is another reason it
is non-negotiable rather than a default to be raised.

**503 is a new status in the committed contract.** `ErrorKind.Unavailable` maps to 503 with a
`Retry-After` header, so `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` are
regenerated. The frontend needs no other change: `client.ts`'s `ApiError` already carries an
arbitrary status.

**Retry converts errors into latency.** Three attempts at two seconds turn a one-second failure
into a seven-second success, and the caller sees slowness rather than a fault. This is the trade
retry always makes; the total timeout bounds it and the standard handler's metrics make it
visible, but it cannot be avoided, only watched.

## Alternatives considered

**Raw `Polly` with a hand-written `DelegatingHandler`.** One fewer package, and full control.
Rejected: it re-derives the strategy ordering above by hand — the part that is actually hard —
and gives up the metrics and `Activity` tags the standard handler emits for free. Control was
not the scarce thing here.

**A retry behavior in the dispatch pipeline**, beside validation, commit and eviction. Tempting
because it would cover every command and query at once with no per-integration work. Rejected,
and not narrowly: a command is not a safe unit of retry. `PlaceOrder` writes an order and an
outbox row in one transaction; replaying it after an ambiguous failure risks a duplicate order,
and the behavior has no way to know which commands are safe. The layer that knows is the one
that owns the side effect.

**`EnableRetryOnFailure` and nothing else.** Genuinely tempting, because it is one line and it
covers the only dependency that exists today. Rejected because the outbound-HTTP shape is the
whole reason this was asked for, and because deciding it later means deciding it under pressure,
in the first integration's pull request, with a deadline attached.

**Registering a resilient client with no consumer**, as a pattern to copy. Rejected: it is dead
code in a repository whose build treats warnings as errors and whose culture treats an untested
registration as an untrue one. The reference integration is small and read-only instead, and it
was chosen because `/api/rates` is the only place where retry, the shared cache factory, the
total timeout and the new `ErrorKind` all meet — which is precisely the interaction a later
author needs to see worked out.

**Widening `IClock` into a `TimeProvider`** so Polly's backoff could be faked through the port
this repository already has. Rejected. `IClock` is the Application port for *domain* time and is
deliberately minimal; `TimeProvider` is a scheduling abstraction Polly happens to need.
Infrastructure's tests can inject `FakeTimeProvider` directly, which costs one registration and
keeps a scheduling concept out of a layer that has no scheduling.
