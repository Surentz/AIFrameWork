# 0009. Query caching: an opt-in pipeline behavior, scoped to the caller

**Date:** 2026-09-09
**Status:** Accepted

## Context

This codebase had no server-side cache of any kind. No `IMemoryCache`, no `IDistributedCache`, no
Redis in either compose file, and nothing in `Directory.Build.props` or any `csproj` that would
bring one in. The one thing that reads like a cache — Wolverine's `BufferedInMemory` local queue
mode — is a message buffer and unrelated.

Caching was therefore a capability the framework was missing, and the reason to add it now is the
pattern rather than any measured latency. There is no slow endpoint to point at. That framing
decided what "done" means here: the deliverable is a correct, hard-to-misuse shape the next
feature can copy, not a number on a graph.

The two reads that exist are, honestly, poor caching candidates. `GetOrderHandler` fetches a
single row by `(id, ownerId)`. `GetOrdersHandler` fetches one keyset page, backed by the index in
`20260902224202_AddOrderPlacedAtIndex`, asking for `Limit + 1` rows so that "is there a next page"
needs no second `COUNT`. Neither is expensive. They were chosen as the call site anyway because
they are the only reads in the repository, and because they exercise the part of caching that
actually goes wrong here.

Both handlers hard-scope to `ICurrentUser.Id` and refuse to run without it, and `GetOrderHandler`
goes further: it has no ownership branch at all, because per ADR 0007 the repository cannot return
another user's order, so someone else's id lands on the same not-found failure as an id that was
never issued. Ownership is enforced inside the query.

A cache placed in front of those handlers inherits that responsibility and can betray it. A cache
key that omits the user serves user A's orders to user B. That failure is silent, it is a data
leak rather than a staleness bug, and no test that merely asserts "the cache works" would catch
it. Every decision below is shaped by that risk more than by throughput.

The design is recorded in full at `docs/superpowers/specs/2026-09-09-query-caching-design.md`.
This ADR condenses it and adds what implementation discovered — including one defect the design
shipped with, which is the most important thing on this page.

## Decision

**The cache stores `TResponse`, never `Result<T>`.** This is a constraint found while designing,
not a preference. `Result<T>` (`src/Application/Abstractions/Result.cs`) is a sealed class with
private fields, an `internal` constructor, and an accessor that throws when read in the wrong
state — `Error` on a success raises `InvalidOperationException("Cannot read Error of a successful
Result.")`. `System.Text.Json` serializing a *successful* `Result<OrderPage>` walks every public
property, reaches `Error`, and throws; deserializing one is impossible regardless, because the
constructor is `internal` and there are no setters. `HybridCache` serializes values even for its
in-process L1 tier unless it can infer the type as immutable, which it does not for `Result<T>`.
So the cache stores the success value — `OrderPage` and `OrderView`, plain records that round-trip
through JSON without ceremony — and `CachedAsync` rebuilds `Result.Success(value)` on a hit.
`Result<T>` was not modified to accommodate the cache: its throwing accessors are a deliberate
feature of the type and outrank the convenience of caching it whole.

**Caching is a query-pipeline behavior, opt-in per query.** The command path already had a
pipeline — `MessagingRegistration.AddCommand` wraps its handler with `Behaviors.ValidateAsync`
before and `Behaviors.CommitAsync` after, inside a `static` local function that captures nothing.
The query path had no wrapper at all. `AddQuery` now delegates to `Behaviors.CachedAsync`, with
handler resolution extracted into `Behaviors.Handle` so the cached and uncached paths share one
definition of "run the handler", and both local functions stay `static` so the no-closure property
the existing code documents is preserved.

Three alternatives were rejected. **Explicit cache calls inside each handler** hide nothing and
let the key use anything in scope, but they repeat in every cached handler and nothing
structurally prevents the next author from omitting the user from the key; in a repository where
the dependency rule and applied migrations are enforced by hooks rather than by discipline,
leaving that leak to discipline is inconsistent. **A caching decorator over `IOrderRepository`**
would leave Application and the handlers untouched, but it caches `Order` entities rather than the
`OrderView`/`OrderPage` the handler returns, and it hides hit-versus-miss from the only layer that
knows who is asking. **HTTP `ETag` and `304`** targets the real cost more directly, since the
frontend refetches on every mount and window focus — but the handler and the database query still
run, it changes the OpenAPI document and the generated client, and it teaches HTTP caching rather
than a cache the Application layer can reach.

Opt-in rather than blanket, and that is a security posture as much as a performance one. `GetUser`
is the only other query in the codebase and it stays uncached; `SignIn`, `RegisterUser`, and
`ChangePassword` are commands and so are outside the cached path entirely. The auth path must stay
that way permanently, not until someone gets round to it: ADR 0008's lockout state served from a
cache would be a security bug, not a stale read.

**The behavior composes the key, so a query cannot forget the caller.** `ICacheable.CacheKey`
supplies only the part that varies with the query's own arguments — `Limit` plus a
prefix-discriminated cursor fragment for `GetOrders`, `Id.ToString()` for `GetOrder`. The query
type name and the caller's id are prepended by the behavior, from `ICurrentUser` resolved inside
Infrastructure. The user scope is not the query author's to write, so it is not theirs to forget.
`CacheScope` (`src/Infrastructure/Messaging/CacheScope.cs`) is the single source of that
composition, because the read side and the eviction side must produce byte-identical scope strings
or eviction misses while every other test stays green: `Tag` is the query name and the user id
joined by a colon, and `Key` is that tag plus another colon and the query's own part, so the tag is
the key's own prefix by construction. That is the whole eviction mechanism.

An `ICacheable` query dispatched with no current user throws, rather than falling back to an
`anon` bucket. An unscoped shared entry is the exact leak this design exists to prevent, and a
cached query is reachable only from an `[Authorize]`d endpoint, so the absence of a caller means
the wiring is wrong. This is deliberately unlike `ValidateAsync`, which tolerates absence because
not every command needs a validator.

**The key prefix is the query's *simple* name, and that choice has a cost worth naming.**
`CachedAsync` composes keys from `typeof(TQuery).Name`. Review proposed `FullName` instead, and it
was rejected: `IInvalidatesCache.Tags` is written as `nameof(GetOrders)`, which yields the simple
name, so a `FullName` key prefix would stop matching its own tag and eviction would silently stop
reaching entries — the worst available failure for this feature, since nothing would report it.
The price of keeping simple names is that two `ICacheable` queries with the same simple name in
different namespaces would share a key prefix and, with the same `CacheKey`, serve one query a
value of the other's `TResponse`. That is for the same user, so it is wrong data rather than a
leak — but wrong data all the same. `EveryCacheableType_HasAUniqueSimpleName` in
`RegistrationCompletenessTests` converts that latent runtime collision into a test failure, so the
trade is paid for by a guard rather than by a comment.

The same class of collision was closed once already inside a single query. `GetOrders.CacheKey`
originally coalesced a null `Cursor` to a fixed literal, which only moved the problem: a client can
send that literal as an ordinary cursor value, and such a request must decode-and-fail like any
other malformed cursor rather than share a key with the no-cursor first page. The key now
discriminates by prefix instead — a bare marker for the null case, and every non-null cursor
prefixed with a different character — so no client-supplied string can collide with the null case
at all.

**`HybridCache`, with L1 only for now.** `Microsoft.Extensions.Caching.Hybrid` ships out-of-band,
supported on .NET 9 and later, and is chosen over a bare `IMemoryCache` for three things that would
otherwise be
hand-rolled: stampede protection, so ten concurrent misses on one key collapse into a single load
instead of ten trips to Postgres; `RemoveByTagAsync`, which is exactly the per-caller invalidation
`PlaceOrder` needs, where the `IMemoryCache` equivalent is a `CancellationChangeToken` per user id
held in a dictionary that then has to be kept from leaking; and an L2 tier later without touching a
call site, which keeps the pattern from being quietly wrong the day a second instance starts.

Redis was rejected for this slice. It would add a third container and a third port to a README
that currently explains two, make every hit pay a network round-trip and a JSON round-trip,
require dev and e2e to run it or degrade gracefully, and need an integration fixture in the shape
of `PostgresFixture`. That is a large amount of machinery for a pattern whose stated purpose is to
be correct rather than fast.

The package is pinned by hand at `10.9.0`, and the version deserves a sentence because it looks
wrong next to its neighbours. `Microsoft.Extensions.Caching.Hybrid` versions on its own release
counter — `9.3.0 … 9.10.0`, then `10.0.0 … 10.9.0` — rather than tracking the shared framework's
patch line, so it does not and cannot match the `10.0.11` on the other `Microsoft.Extensions.*`
references. What mattered was the API surface, and that was verified by compile probe: the
six-argument `GetOrCreateAsync`, `HybridCacheEntryOptions.Expiration`, `AddHybridCache`'s options
callback, and `RemoveByTagAsync` returning `ValueTask`.

**Invalidation is synchronous, in the command pipeline.** A command declares the query types it
invalidates via `IInvalidatesCache`; `Behaviors.EvictAsync` calls `RemoveByTagAsync` for each,
after `CommitAsync` has succeeded, in-request, before the response returns. Read-your-own-writes
is the reason. The frontend's `usePlaceOrder` invalidates `orderKeys.all` in `onSuccess`, so a
refetch of `GET /api/orders` arrives milliseconds after the `201` and must not be served a page
that omits the order just created. That is the bug a user reports as "it didn't save".

Riding the existing domain-event path was the natural-looking alternative and it does not work
here. `OrderPlaced` is delivered through the outbox, which `OutboxPoller` polls, so the `201`
returns before any handler runs and the immediate refetch reads a stale page. It would also force
`OrderPlaced` — today `(Guid OrderId, string Sku, int Quantity)` — to grow an `OwnerId`, because
the outbox pumps have no HTTP context and no `ICurrentUser`. Changing a Domain event's shape, and
the payload the outbox already serializes, to serve a caching concern is the wrong direction of
dependency. TTL-only, with no invalidation at all, was rejected too: it would leave the
framework's caching pattern with no answer for writes, which is the first question the next
feature asks.

**The TTL lives on the query; the cap and the kill switch live in configuration.**
`ICacheable.Duration` is a literal on the query record — thirty seconds on both order reads, long
enough that a refocus-driven refetch is a hit and short enough that a write from another device
appears without anyone waiting for it. `GetOrders` is the type that knows how stale its own page
may acceptably be, and a record in Application has nothing to inject configuration through.
`CacheOptions` therefore holds only what an operator needs to reach without a deploy: `Enabled`
(true) and `MaximumDuration` (one minute), a ceiling `CacheDuration.Clamp` applies to every
`Duration` so a careless literal in Application cannot retain a page for an hour. It is a cap, not
a default: it never raises a duration. `AddCaching` validates it the way `AddOutbox` validates
`OutboxOptions` — positive, and no more than an hour — so a value that is merely wrong rather than
malformed fails loudly at startup instead of caching quietly forever.

`AddCaching` deliberately does not bind the configuration section itself; `Program.cs` does, the
same way it already reads `Wolverine:Durable` and `RateLimiting:Auth`. Binding inside `AddCaching`
would make `IOptions<CacheOptions>` depend on an `IConfiguration` being registered, which a bare
`ServiceCollection` in a unit test does not have — and `CachedAsync` resolves those options on
every single query.

**Off in tests and e2e, on deliberately where the cache is the subject.** `ApiFactory` sets
`Cache:Enabled=false` through `builder.UseSetting`, alongside the `RateLimiting:Auth:PermitLimit`
it already sets for the same reason, and the e2e API gets `Cache__Enabled: 'false'` in
`frontend/playwright.config.ts`'s `webServer` env — *not* in `docker-compose.e2e.yml`, which runs
only Postgres and no API. This follows ADR 0008's limiter precedent in the literal sense of being
the same two files, so every existing order test runs against the behavior it was written for.
`tests/Api.IntegrationTests/Orders/OrderCachingTests.cs` is where the cache is switched back on:
the same split `AuthRateLimitTests` uses for the limiter, though by a cheaper route — it stays in
`ApiFactoryCollection` and layers a second host over it with `WithWebHostBuilder` and
`Cache:Enabled=true`, which composes over `ApiFactory.ConfigureWebHost` and so keeps the one
shared Postgres container rather than starting another.

Running the cache on everywhere — one code path in every environment — is genuinely attractive
given what the root `CLAUDE.md` records about Release breaking unnoticed for the life of the
Wolverine spike. It was rejected because the first flake would be debugged as a test bug rather
than as a cache hit, and because turning the cache on deliberately in the tests that are *about*
the cache buys the same coverage without that cost. Keeping the pipeline wired with a zero TTL was
rejected as well: the interesting behavior is what happens on a hit, and a zero TTL guarantees the
suite never sees one.

**Failures are kept out of the cache by a sentinel exception, not by a removal.** The factory
inside `GetOrCreateAsync` runs the handler and throws a `private sealed` `QueryFailedException`
carrying the `Error` when the `Result` failed; `CachedAsync` catches that specific type and returns
`Result.Failure<TResponse>`. `HybridCache` stores nothing when its factory throws, so a 404 is not
retained for the full duration, no removal round-trip is needed, and there is no window in which a
concurrent caller reads a cached failure. Caching a wrapper and removing it afterwards would leave
exactly that window. The sentinel carries this branch's only new suppression in `src/` — a
`#pragma warning disable CA1032, S3871` pair, because both rules ask to widen a type whose entire
purpose is to stay inside one method, and the `catch` names that type rather than `Exception`, so
CA1031 is satisfied on its own terms. The branch adds one further suppression, in the test
projects: a `CA1711` on the `CacheBehaviorCollection` marker, which is the established xUnit
collection-definition idiom already suppressed the same way in `ApiFactory` and `PostgresFixture`.

### The defect the design shipped with: `HybridCache` runs its factory without the `HttpContext`

This is the one thing implementation found that the design did not anticipate, and it broke every
cached read.

`HybridCache.GetOrCreateAsync` runs its factory **without the ambient `HttpContext`** — by design,
and correctly: a computation that may be shared between concurrent callers must not depend on any
one caller's ambient state. The design runs the query handler inside that factory. `CurrentUser`
(`src/Api/Auth/CurrentUser.cs`) exposed `Id` as a *live* property, re-reading
`accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)` on every access. So on
every cache miss the handler's own auth guard saw `null`, returned `ErrorKind.Unauthorized`, and
the endpoint answered **401**. Not a stale read and not a leak — the feature simply did not work,
and only on a miss.

The cache key was never affected, which is why the failure was confusing rather than obviously
fatal: `CachedAsync` reads `ICurrentUser.Id` *before* entering the factory, so the key was always
correctly scoped. Only the handler running inside the factory saw nothing.

The fix is that `CurrentUser` now memoizes the **first non-null** id (`_id ??= …`). Non-null-only
is deliberate: a read before authentication still returns `null` and retries, so nothing that
reads `Id` pre-sign-in changed behavior. `ICurrentUser` in `src/Application/Abstractions/Ports.cs`
now documents the resulting contract — once `Id` resolves to a non-null value within a scope it
must keep returning that value — because a second implementation that re-read its source on every
access would silently reintroduce the 401. `CurrentUserTests` pins both halves: a read after the
`HttpContext` is gone still returns the memoized id, and a signed-out-then-signed-in read returns
the new one rather than sticking on the first null. The memo is unsynchronized on purpose, and the
comment at the field says why: `Guid?` is not written atomically, so a torn read could fabricate a
user id rather than merely return a stale null — safe today only because nothing in this codebase
dispatches queries in parallel within one request scope, and that comment is where to notice if
that ever changes.

Two alternatives were rejected. **Threading the resolved id into the factory's state tuple** would
work, but it forces `ICurrentUser` out of the handler constructors, which is Application-layer
churn across every cached handler for the benefit of one Infrastructure behavior. **Restoring
`accessor.HttpContext` inside the factory** would be actively wrong for a joined stampede caller —
it would hand them the originating request's context — besides pushing Api knowledge into an
Infrastructure behavior.

**Why no earlier test caught it.** The behavior tests register `ICurrentUser` as a substituted
singleton, which has no ambient-context dependence at all and therefore cannot reproduce the
failure. Only a real HTTP request carrying a real cookie could expose it, which is what the
integration tests were for — and did: `OrderCachingTests` is where it surfaced, and it blocked
that work until the memoization landed. The lesson worth keeping is not "write more tests" but
that a substituted port can hide a dependency on ambient state that the real implementation has
and the interface never mentioned; the `ICurrentUser` XML remark exists so the interface mentions
it now.

## Consequences

**No OpenAPI regeneration.** No controller, DTO, or `[ProducesResponseType]` changed, so
`openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` are untouched and CI's
`contract` job needs nothing.

**No Wolverine codegen.** No handler was added or changed, so nothing under
`src/Api/Internal/Generated` moved and `WolverineCodegenTests` has nothing new to catch.

**No migration.** Nothing is persisted.

**The publish delta is measured, not assumed.** A Release publish before adding
`Microsoft.Extensions.Caching.Hybrid` is 19,496,507 bytes; after, 19,595,953 bytes. That is 99,446
bytes — about 97 KB — and it is exactly one new file, `Microsoft.Extensions.Caching.Hybrid.dll`,
with no new transitive assemblies. The one-new-assembly detail is why the delta is so small beside
the numbers this repository records elsewhere: 7.9MB to 37MB for
`Microsoft.EntityFrameworkCore.Design`, and 17MB to 50MB for Roslyn, both of which drag in a
compiler. Measured at `du -sm`'s 1MB granularity this delta rounds away entirely, which is why it
is recorded in bytes.

**`HybridCache` does not log the sentinel exception, and that was verified rather than assumed.**
The sentinel design raised a fair objection: if `HybridCache` logged a factory exception at error
level, every ordinary 404 on a cacheable query would emit an error entry with a stack trace, and a
correctness feature would become an operational nuisance. It does not. Re-confirmed at
`--verbosity normal` on the real `NotFound` path, with zero matches for `HybridCache`,
`QueryFailedException`, or the sentinel anywhere in the output — in a run where Error-level EF Core
and Wolverine lines *did* appear, so the absence is evidence rather than a quiet log level.

`RegistrationCompletenessTests` gained three guards, all green on arrival because they protect
against a future mistake rather than a present one: an `ICacheable` type that is not a registered
query caches nothing, since the marker only takes effect inside `AddQuery`'s dispatch delegate; an
`IInvalidatesCache` command with no tags evicts nothing; and two `ICacheable` types may not share a
simple name, for the key-prefix reason above. All three are no-ops that read as protection, which
is the class of mistake that file exists for.

No test waits for a TTL to lapse, and none can: `tests/CLAUDE.md` forbids `Thread.Sleep`, and
`HybridCache` expires entries on its own internal clock, which this repository's `IClock` cannot
reach. Expiry is therefore deliberately unobserved by the suite. The only TTL arithmetic —
`CacheDuration.Clamp` — was extracted precisely so it could be tested directly, with no host, no
cache, and no waiting. What remains untested is that `HybridCache` honors the `Expiration` it is
handed, which is the library's contract rather than this repository's.

### Accepted risks

**The key space is unbounded in principle.** Any cursor string is a distinct key, so a client
looping over generated cursors can create entries freely. It is bounded in practice by a TTL of at
most sixty seconds and by how deep anyone actually pages, and each entry is bounded by
`MaximumPayloadBytes` (1MB), but nothing bounds the count. If L1 growth ever becomes visible, the
answer is a `SizeLimit`-ed `MemoryCache` behind `HybridCache`; that is a follow-up, not part of
this decision.

**A failed eviction is not caught.** With L1 only, `RemoveByTagAsync` has no realistic failure
mode, and catching one would mean `catch (Exception)` on a path with no `IExceptionHandler`
parameter — banned everywhere outside the outbox's file-scoped exemption. So it propagates, and a
committed write would surface a 500 to a caller whose order was in fact saved. That is an
acceptable trade only while the cache is in-process. **It becomes the wrong trade the day an L2
tier is added**, because a network hop has an entirely realistic failure mode; revisit it alongside
that change rather than after it. There is a second, likelier trigger for the same
post-commit-500 today, with no L2 needed: `EvictAsync`'s own fail-loud `throw` fires *after*
`CommitAsync` has already succeeded, for exactly the same reason `ICacheable` refuses to fall back
to an anonymous bucket — an `IInvalidatesCache` command with no current user is a wiring error, not
something to tolerate. It is unreachable today only because `PlaceOrderHandler` returns
`Unauthorized` when there is no caller, so the `Result` is already unsuccessful and `EvictAsync`
returns before it ever reaches that throw. A future `IInvalidatesCache` command that legitimately
succeeds with no `ICurrentUser` — one dispatched from the outbox, say — would commit and then 500
on the way out, by this path rather than `RemoveByTagAsync`'s.

**A joined stampede caller can be handed an `ObjectDisposedException`.** The factory runs the
handler on request-scoped dependencies, and `HybridCache` may share one factory's result across
concurrent callers on the same key. If the originating request is aborted, its scope can be
disposed while a joined caller is still awaiting that work, and the joined caller sees the
`DbContext` disposed underneath it — surfacing as a 500. This is not a data-correctness or
cross-user problem: keys carry the caller's id, so a joined caller is by construction the same
user, which is a second reason the user scoping is load-bearing rather than merely careful, and it
degrades to an error rather than to wrong data. It is recorded here rather than in a backlog
because opting `GetOrders` and `GetOrder` in is what makes it reachable in production for the
first time.

## Alternatives considered

The rejected shapes are argued where the decision they belong to is made, above: in-handler cache
calls, an `IOrderRepository` decorator, and HTTP `ETag`/`304` for the mechanism; Redis for the
store; an event-driven eviction off the outbox and TTL-only with no invalidation for the write
path; `FullName` key prefixes; running the cache on in every environment, and running it with a
zero TTL. Two more are worth recording because they were attractive on their own terms:

**Fixing the frontend's query defaults instead.** `frontend/src/main.tsx` is a bare
`new QueryClient()`, so `staleTime` is 0 and every mount and every window refocus refetches. That
is a real problem, and cutting those refetches would reduce load more than a thirty-second
server-side cache does. It is deliberately out of scope rather than dismissed: it is a separate
design, and putting both behind one ADR would leave neither argued properly.

**Caching anything on the auth path.** Deliberately permanent, not deferred. ADR 0008's lockout
state must be read from the database every time, and a cached read there is a security bug rather
than a stale one.

---

**Amended by ADR 0010.** The single-process premise this ADR was written on — stated explicitly
in `Behaviors.cs`'s comment that "with an L1-only HybridCache `RemoveByTagAsync` has no realistic
failure mode" — no longer holds once the API runs at more than one replica. ADR 0010 records how
cache eviction correctness is preserved across replicas (ingress cookie session affinity) without
changing anything decided above.
