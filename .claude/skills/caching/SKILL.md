---
name: caching
description: Use when making a query cacheable, adding cache eviction to a command, or debugging stale or cross-user cached data - ICacheable, IInvalidatesCache, CacheScope keys and tags, HybridCache being L1-only, and why the cache is off under test.
---

# Caching

Queries opt in by implementing `ICacheable` (`src/Application/Abstractions/Caching.cs`); commands
opt in to eviction with `IInvalidatesCache`. `GetOrders` and `GetOrder` are cached at thirty
seconds; `PlaceOrder`, `ShipOrder` and `CancelOrder` evict both for the caller. `GetProducts` and
`GetProduct` are cached the same way, and `CreateProduct`/`UpdateProduct` evict them.
`GetExchangeRate` is cached for a minute. Nothing on the auth path is cached, deliberately and
permanently — ADR 0008's lockout state must be read every time — and neither are the
notification feed, the fulfilment queue or the monitoring reads, each for reasons on its own
query.

Four things that will cost you time:

- **The cache stores `TResponse`, not `Result<T>`.** `Result<T>.Error` throws when read on a
  success, so `System.Text.Json` fails on a successful `Result` and the `internal` constructor
  makes deserializing one impossible. The behavior rebuilds `Result.Success(value)` on a hit.
- **`CacheKey` must NOT contain a user id.** The behavior prepends the query type and
  `ICurrentUser.Id` via `CacheScope`, which is also what `IInvalidatesCache.Tags` is composed
  with — the tag is the key's own prefix, and that is the whole eviction mechanism. An
  `ICacheable` query dispatched with no current user throws rather than sharing one entry across
  every caller.
- **`ICurrentUser.Id` must stay stable once resolved.** `HybridCache` runs its cache-miss factory
  without the ambient `HttpContext`, by design, so a *live* implementation reading the claim on
  every access reports the caller unauthenticated inside the factory and every cache miss answers
  401. `CurrentUser` memoizes the first non-null id for exactly this reason; a second
  implementation of the port that re-reads its source reintroduces the bug silently.
- **The cache is OFF under test.** `ApiFactory` sets `Cache:Enabled=false` and
  `frontend/playwright.config.ts`'s `webServer` env sets `Cache__Enabled=false` — not
  `docker-compose.e2e.yml`, which runs only Postgres and RabbitMQ. The worker runs with it off
  everywhere. `Orders/OrderCachingTests` turns it back on
  for itself — `WithWebHostBuilder` over the shared `ApiFactory`, so it keeps the one Postgres
  container — the same split `AuthRateLimitTests` uses for the rate limiter.

**The catalogue is the one cached read that is not per-user data.** Products are global, but the
cache is scoped per caller by construction, so `CreateProduct`'s eviction reaches only the caller
who made the write — everyone else keeps their cached pages until the thirty seconds lapse. The
TTL, not the eviction, is what bounds how long an edit stays invisible to other people. That is
accepted rather than worked around: an unscoped cache path would give up the one property that
makes this cache safe to use without thinking. See ADR 0013.

**The same scoping applies when someone else changes your order.** An administrator shipping
from the fulfilment queue evicts the *administrator's* entries, not the buyer's, and a warehouse's
`RecordShipment` runs in the worker with no caller at all — so the buyer's cached `GetOrder`/
`GetOrders` can show the old status for up to thirty seconds. That is accepted in ADR 0024; the
notification, which is never cached, is what tells the buyer promptly.

`PlaceOrder` snapshots the product's name and price onto the order, so a later `UpdateProduct`
cannot change what an existing order says it cost — and cannot stale a cached order page either.
That is what keeps product writes out of the order cache's eviction path entirely.

No test waits for a TTL to lapse; `HybridCache` expires on its own clock, which `IClock` cannot
reach. The only TTL arithmetic is `CacheDuration.Clamp`, tested directly.

See ADR 0009.

