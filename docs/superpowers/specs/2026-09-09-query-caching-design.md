# Query caching: an opt-in pipeline behavior, scoped to the caller

**Date:** 2026-09-09
**Status:** Approved, not yet implemented

## Context

This codebase has no server-side cache. There is no `IMemoryCache`, no `IDistributedCache`, no
Redis in either compose file, and nothing in `Directory.Build.props` or any `csproj` that would
bring one in. The only thing in the repository that reads like a cache is Wolverine's
`BufferedInMemory` local queue mode, which is a message buffer and unrelated.

Caching is therefore a capability the framework is missing, and the reason to add it now is the
pattern rather than any measured latency. There is no slow endpoint to point at. That framing
matters, because it decides what "done" means: the deliverable is a correct, hard-to-misuse shape
that the next feature can copy, not a number on a graph.

The two reads that exist are, honestly, poor caching candidates. `GetOrderHandler` fetches a
single row by `(id, ownerId)`. `GetOrdersHandler` fetches one keyset page, backed by the index
added in `20260902224202_AddOrderPlacedAtIndex`, and asks for `Limit + 1` rows so that "is there a
next page" needs no second `COUNT`. Neither is expensive. They were chosen as the call site anyway
because they are the only reads in the repository, and because they exercise the part of caching
that actually goes wrong here.

Both handlers hard-scope to `ICurrentUser.Id` and refuse to run without it:

```csharp
if (currentUser.Id is not { } userId)
{
    return Result.Failure<OrderPage>(new Error(
        ErrorKind.Unauthorized, "auth.failed", "That session is no longer valid."));
}
```

`GetOrderHandler` goes further, and its comment says why: "No ownership branch: the repository
cannot return another user's order, so someone else's id lands on the same not-found failure as an
id that was never issued." Ownership is enforced in the query itself, per ADR 0007.

A cache placed in front of those handlers inherits that responsibility and can betray it. A cache
key that omits the user serves user A's orders to user B. That failure is silent, it is a data
leak rather than a staleness bug, and no test that merely asserts "the cache works" would catch
it. Every decision below is shaped by that risk more than by throughput.

There is one nearby precedent to reuse. Commit `403de79`, "keep the auth limiter out of the
Playwright suite's way", is the same problem one feature earlier: a correctness feature that
changes behavior under test and has to be switched off where it is not the subject.

## Decisions

### The cache stores `TResponse`, never `Result<T>`

This is a constraint discovered while designing, not a preference. `Result<T>` in
`src/Application/Abstractions/Result.cs` is a sealed class with private fields, an `internal`
constructor, and accessors that throw when read in the wrong state:

```csharp
public Error Error => IsSuccess
    ? throw new InvalidOperationException("Cannot read Error of a successful Result.")
    : _error!;
```

`System.Text.Json` serializing a *successful* `Result<OrderPage>` walks every public property,
reaches `Error`, and throws. Deserializing one is impossible regardless: the constructor is
`internal` and there are no setters. `HybridCache` serializes values even for its in-process L1
tier unless it infers the type as immutable, which it does not for `Result<T>`.

So the cache stores the success value — `OrderPage` and `OrderView`, plain records with public
primary constructors that round-trip through JSON without ceremony — and the behavior rebuilds
`Result.Success(value)` on a hit. `Result<T>` is not modified to accommodate the cache; its
throwing accessors are a deliberate feature of the type and outrank the convenience of caching it
whole.

### Caching is a query-pipeline behavior, opt-in per query

The command path already has a pipeline. `MessagingRegistration.AddCommand` wraps its handler with
`Behaviors.ValidateAsync` before and `Behaviors.CommitAsync` after, inside a `static` local
function that captures nothing so there is no closure allocation. The query path has no wrapper at
all — `AddQuery`'s `InvokeAsync` resolves the handler and calls it.

Caching attaches by giving the query path the same kind of wrapper, and a query opts in by
implementing a marker interface. The alternatives were considered and rejected:

- **Explicit calls inside each handler.** Nothing hidden, and the key can use anything in scope.
  But it repeats in every cached handler, and nothing structurally prevents the next author from
  omitting the user from the key. In a repository where the dependency rule and applied migrations
  are enforced by hooks rather than by discipline, leaving the leak to discipline is inconsistent.
- **A caching decorator over `IOrderRepository`.** Application and the handlers would not change
  at all. But it caches `Order` entities rather than the `OrderView`/`OrderPage` the handler
  returns, and it hides hit-versus-miss from the only layer that knows who is asking.
- **HTTP-level `ETag` and `304`.** This targets the real cost more directly, since the frontend
  refetches on every mount and window focus. But the handler and the database query still run, it
  changes the OpenAPI document and the generated client, and it teaches HTTP caching rather than a
  cache the Application layer can reach.

Opt-in rather than blanket: a query is cached only when its author says so. `GetUser`, `SignIn`,
`RegisterUser`, and `ChangePassword` stay uncached, and the auth path in particular must remain
so — ADR 0008's lockout state read from a cache would be a security bug, not a stale read.

### The behavior composes the key; a query cannot forget the caller

`ICacheable` supplies only the part of the key that varies with the query's own arguments. The
query type name and the caller's id are prepended by the behavior, from `ICurrentUser` resolved
inside Infrastructure. The user scope is not the query author's to write, so it is not theirs to
forget.

A single helper owns the composition, because the read side and the eviction side must produce
byte-identical scope strings or eviction misses silently while every other test stays green:

```csharp
// Infrastructure/Messaging/CacheScope.cs
internal static class CacheScope
{
    // "GetOrders:3f2a...:20:"  — the tag is the key's own prefix, by construction.
    internal static string Key(string queryName, Guid userId, string part) =>
        $"{Tag(queryName, userId)}:{part}";

    internal static string Tag(string queryName, Guid userId) =>
        $"{queryName}:{userId}";
}
```

An `ICacheable` query dispatched with no current user is a hard failure, not an `anon` bucket. An
unscoped shared entry is the exact leak this design exists to prevent, and a cached query is
reachable only from an `[Authorize]`d endpoint, so the absence of a caller means the wiring is
wrong. This is deliberately unlike the validation behavior, which tolerates absence because not
every command needs a validator.

### `HybridCache`, with L1 only for now

`Microsoft.Extensions.Caching.Hybrid` has been in-box since .NET 9 and suits `net10.0`. It is
chosen over a bare `IMemoryCache` for three things that would otherwise be hand-rolled:

- **Stampede protection.** Ten concurrent misses on one key collapse into a single load. With
  `IMemoryCache` all ten reach Postgres.
- **`RemoveByTagAsync`.** Per-caller invalidation by tag is exactly the operation `PlaceOrder`
  needs. The `IMemoryCache` equivalent is a `CancellationChangeToken` per user id, held in a
  dictionary that then has to be kept from leaking.
- **An L2 tier later without touching a call site.** Registering a distributed cache is the only
  change required, which keeps the pattern from being quietly wrong the day a second instance
  starts.

Redis was rejected for this slice. It would add a third container and a third port to a README
that currently explains two, make every hit pay a network round-trip and a JSON round-trip,
require dev and e2e to run it or degrade gracefully, and need an integration fixture in the shape
of `PostgresFixture`. That is a large amount of machinery for a pattern whose stated purpose is to
be correct rather than fast.

The package is added to `src/Infrastructure/AiFramework.Infrastructure.csproj` with an explicit
hand-pinned version on the same `10.0.x` line as the other `Microsoft.Extensions` references,
matching how every other dependency in this repository is pinned. There is no central package
management here.

### Invalidation is synchronous, in the command pipeline

A command declares the tags it invalidates via `IInvalidatesCache`. `Behaviors.EvictAsync` calls
`RemoveByTagAsync` for each, after `CommitAsync` has succeeded, in-request, before the response
returns.

Synchronous is the decision, and read-your-own-writes is the reason. The frontend's
`usePlaceOrder` invalidates `orderKeys.all` in `onSuccess`, so a refetch of `GET /api/orders`
arrives milliseconds after the `201`. It must not be served a page that omits the order just
created. That is the bug a user reports as "it didn't save".

Riding the existing domain-event path was the natural-looking alternative and it does not work
here. `OrderPlaced` is delivered through the outbox, which is polled by `OutboxPoller`, so the
`201` returns before any handler runs and the immediate refetch reads a stale page. It would also
require `OrderPlaced` — today `(Guid OrderId, string Sku, int Quantity)` — to carry an `OwnerId`,
because the outbox pumps have no HTTP context and no `ICurrentUser`. Changing a Domain event's
shape, and the payload the outbox already serializes, to serve a caching concern is the wrong
direction of dependency.

TTL-only, with no invalidation at all, was also rejected. It leaves the framework's caching
pattern with no answer for writes, which is the first question the next feature asks.

### The TTL lives on the query; the cap and the kill switch live in config

`ICacheable.Duration` is a literal on the query record. `GetOrders` is the type that knows how
stale its own page may acceptably be, and a record in Application has nothing to inject
configuration through.

`CacheOptions` therefore holds only what an operator needs to reach without a deploy: `Enabled`,
and `MaximumDuration` as a ceiling the behavior clamps every `Duration` to, so a careless literal
in Application cannot retain a page for an hour. `MaximumDuration` is a cap, not a default.

Registration mirrors `AddOutbox()`, including its validation, so a bad value fails loudly at
startup instead of silently caching forever — the same reasoning `OutboxOptions` records for
`WorkerCount = 0`.

### Off in tests and e2e, on deliberately where the cache is the subject

`ApiFactory` sets `Cache:Enabled=false` via `builder.UseSetting`, alongside the
`RateLimiting:Auth:PermitLimit` it already sets for the same reason. The e2e API gets
`Cache__Enabled: 'false'` in `frontend/playwright.config.ts`'s `webServer[0].env` — *not* in
`docker-compose.e2e.yml`, which runs only Postgres and no API. That env block is where
`RateLimiting__Auth__PermitLimit: '1000000'` already lives, so this follows `403de79` in the
literal sense of being the same two files.

Every existing order test then runs against the behavior it was written for.

The alternative — on everywhere, one code path in every environment — is genuinely attractive
given what the CI section of the root `CLAUDE.md` says about Release breaking unnoticed. It was
rejected because the first flake would be debugged as a test bug rather than as a cache hit, and
because turning the cache on deliberately in the tests that are *about* the cache gives the same
coverage without that cost. Keeping the pipeline wired but with a zero TTL was rejected too: the
interesting behavior is what happens on a hit, and a zero TTL guarantees the suite never sees one.

## What is cached, and what is not

| Type | Change | Key part | TTL |
|---|---|---|---|
| `GetOrders` | `+ ICacheable` | `$"{Limit}:{Cursor}"` | 30 seconds |
| `GetOrder` | `+ ICacheable` | `$"{Id}"` | 30 seconds |
| `PlaceOrder` | `+ IInvalidatesCache` | tags `[nameof(GetOrders), nameof(GetOrder)]` | — |

Everything else is untouched: `GetUser`, `SignIn`, `RegisterUser`, `ChangePassword`, every
repository, every domain event handler, and the outbox.

## Contracts

Two plain interfaces in Application. No caching package crosses into that layer — `ICacheable`
declares intent, and Infrastructure alone chooses the store.

```csharp
// src/Application/Abstractions/Caching.cs

/// <summary>
/// A query whose successful result may be cached. CacheKey is only the part that varies with
/// this query's own arguments — the behavior prepends the query type and the caller's id, so a
/// query cannot omit the user scope and leak another caller's data.
/// </summary>
public interface ICacheable
{
    public string CacheKey { get; }
    public TimeSpan Duration { get; }
}

/// <summary>
/// A command that invalidates cached queries belonging to the caller who issued it. Tags name
/// query types; the caller's id is prepended by the behavior, as with keys.
/// </summary>
public interface IInvalidatesCache
{
    public IReadOnlyList<string> Tags { get; }
}
```

## The behavior

`Behaviors.CachedAsync` joins `ValidateAsync` and `CommitAsync` in
`src/Infrastructure/Messaging/Behaviors.cs`, and preserves the no-capture shape of the existing
pipeline: `sp` and the query ride in as `TState`, so the factory stays a `static` lambda.

```csharp
internal static async Task<Result<TResponse>> CachedAsync<TQuery, TResponse>(
    IServiceProvider sp, TQuery query, CancellationToken ct)
    where TQuery : IQuery<TResponse>
{
    var options = sp.GetRequiredService<IOptions<CacheOptions>>().Value;

    if (!options.Enabled || query is not ICacheable cacheable)
    {
        return await Handle<TQuery, TResponse>(sp, query, ct).ConfigureAwait(false);
    }

    // Not absence-tolerant, unlike the validator: a cached query with no caller to scope to
    // would share one entry across every user. Fail loudly rather than leak.
    var userId = sp.GetRequiredService<ICurrentUser>().Id
        ?? throw new InvalidOperationException(
            $"'{typeof(TQuery).Name}' is ICacheable but there is no current user to scope its " +
            "key to. Cached queries must be reachable only from an authorized endpoint.");

    var cache = sp.GetRequiredService<HybridCache>();
    var name = typeof(TQuery).Name;
    var duration = CacheDuration.Clamp(cacheable.Duration, options.MaximumDuration);

    try
    {
        var value = await cache.GetOrCreateAsync(
            CacheScope.Key(name, userId, cacheable.CacheKey),
            (sp, query),
            static async (state, ct) =>
            {
                var result = await Handle<TQuery, TResponse>(state.sp, state.query, ct)
                    .ConfigureAwait(false);

                // The sentinel keeps failures out of the cache. HybridCache stores nothing when
                // a factory throws, so a 404 is not retained for the full duration, no removal
                // round-trip is needed, and there is no window in which a concurrent caller
                // reads a cached failure. QueryFailed is private to this file and never escapes
                // the catch below, which names it specifically rather than catching Exception.
                return result.IsSuccess ? result.Value : throw new QueryFailed(result.Error);
            },
            new HybridCacheEntryOptions { Expiration = duration },
            tags: [CacheScope.Tag(name, userId)],
            cancellationToken: ct).ConfigureAwait(false);

        return Result.Success(value);
    }
    catch (QueryFailed failed)
    {
        return Result.Failure<TResponse>(failed.Error);
    }
}
```

`EvictAsync` mirrors it and runs only after a commit that succeeded:

```csharp
internal static async Task EvictAsync<TCommand, TResponse>(
    IServiceProvider sp, TCommand command, Result<TResponse> result, CancellationToken ct)
{
    if (!result.IsSuccess || command is not IInvalidatesCache invalidates)
    {
        return;
    }

    if (!sp.GetRequiredService<IOptions<CacheOptions>>().Value.Enabled)
    {
        return;
    }

    var userId = sp.GetRequiredService<ICurrentUser>().Id
        ?? throw new InvalidOperationException(
            $"'{typeof(TCommand).Name}' invalidates cache tags but there is no current user " +
            "to scope them to.");

    var cache = sp.GetRequiredService<HybridCache>();

    foreach (var tag in invalidates.Tags)
    {
        await cache.RemoveByTagAsync(CacheScope.Tag(tag, userId), ct).ConfigureAwait(false);
    }
}
```

`MessagingRegistration` changes in two places. `AddQuery`'s local function delegates to
`CachedAsync` instead of resolving the handler itself — the handler resolution moves into a
`Behaviors.Handle` helper that both the cached and uncached paths call — and `AddCommand` gains one
line after the existing commit:

```csharp
// AddCommand, after the existing CommitAsync
await Behaviors.CommitAsync(sp, result, ct).ConfigureAwait(false);
await Behaviors.EvictAsync(sp, typed, result, ct).ConfigureAwait(false);

return result;
```

Both local functions stay `static`, so the no-closure property the current code documents is
preserved.

A malformed cursor never becomes a cache entry. `GetOrdersHandler` validates its own inputs
internally — the query dispatcher runs no validation behavior, which is command-only — so
validation now happens behind the cache lookup. A validation failure is a failed `Result`, the
factory throws the sentinel, and nothing is stored. The lookup itself happens against a junk key
and misses; that is the whole effect.

## Configuration

```csharp
// src/Infrastructure/Caching/CacheOptions.cs — beside Outbox/OutboxOptions.cs
public sealed class CacheOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A ceiling the behavior clamps every ICacheable.Duration to, so a careless literal in
    /// Application cannot retain a page for an hour. A cap, not a default.
    /// </summary>
    public TimeSpan MaximumDuration { get; set; } = TimeSpan.FromMinutes(1);
}

// src/Infrastructure/Caching/CachingRegistration.cs
public static IServiceCollection AddCaching(this IServiceCollection services)
{
    ArgumentNullException.ThrowIfNull(services);

    services.AddOptions<CacheOptions>()
        .Validate(o => o.MaximumDuration > TimeSpan.Zero,
            "CacheOptions.MaximumDuration must be positive.")
        .Validate(o => o.MaximumDuration <= TimeSpan.FromHours(1),
            "CacheOptions.MaximumDuration above an hour is almost certainly a mistake.");

    services.AddHybridCache(o => o.MaximumPayloadBytes = 1024 * 1024);

    return services;
}
```

Called from `AddInfrastructure` beside `AddOutbox()`. `Api` still reaches nothing past that single
entry point. `Program.cs` binds the `Cache` section and `src/Api/appsettings.json` carries
`"Cache": { "Enabled": true }`.

## Testing

The tests that matter are the two silent failures, not "does the cache work".

`tests/Infrastructure.Tests/Messaging/BehaviorTests.cs`, with a real `HybridCache` and an
NSubstitute `ICurrentUser`:

| Test | Guards |
|---|---|
| `Second_dispatch_of_a_cacheable_query_does_not_reach_the_handler` | the mechanism |
| `A_second_user_gets_their_own_entry` | the leak — same `CacheKey`, different `ICurrentUser.Id`, must miss and must not return the first user's value |
| `Eviction_removes_what_the_query_wrote` | the drift — writes via `CachedAsync`, evicts via `EvictAsync`, asserts the next read misses; the only thing proving `Key` and `Tag` still agree |
| `A_cacheable_query_with_no_current_user_throws` | the fail-loud path rather than an unscoped shared entry |
| `A_failed_result_is_not_cached` | the sentinel — a second dispatch reaches the handler again |
| `Clamp_never_exceeds_MaximumDuration` | the cap, as a pure function — see below |
| `Disabled_options_bypass_the_cache_entirely` | the kill switch the other suites depend on |

**No test waits for a TTL to expire, and none can.** `tests/CLAUDE.md` is explicit — "No
`Thread.Sleep`. Inject an `IClock`" — and `HybridCache` expires entries on its own internal clock,
which this repository's `IClock` cannot reach. So expiry is deliberately unobserved by the suite.

The clamp is therefore extracted as a pure function rather than left inline in `CachedAsync`,
which is where the only interesting arithmetic lives and the only part of the TTL that is behavior
rather than a value:

```csharp
// Infrastructure/Caching/CacheDuration.cs
internal static class CacheDuration
{
    internal static TimeSpan Clamp(TimeSpan requested, TimeSpan maximum) =>
        requested < maximum ? requested : maximum;
}
```

`CachedAsync` calls it, and `Clamp_never_exceeds_MaximumDuration` tests it directly with no host,
no cache, and no waiting. What remains untested is that `HybridCache` honors the `Expiration` it
is handed — which is the library's contract, not this repository's.

`RegistrationCompletenessTests` is extended so `AddCaching`'s registrations fall under the same
rule that already fails the build on a missed handler.

`tests/Api.IntegrationTests/Orders/OrderCachingTests.cs` turns the cache on deliberately for three
end-to-end cases: a served hit; a cross-user miss over real HTTP with two real sessions; and
`PlaceOrder` followed immediately by `GET /api/orders` returning the new order. The last is the
read-your-own-writes guarantee the synchronous-eviction decision exists for.

## Consequences

**No OpenAPI regeneration.** No controller, DTO, or `[ProducesResponseType]` changes, so
`openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` are unaffected and CI's
`contract` job needs nothing.

**No Wolverine codegen.** No handler is added or changed, so nothing under
`src/Api/Internal/Generated` moves and `WolverineCodegenTests` has nothing new to catch.

**No migration.** Nothing is persisted.

**The publish-size delta is measured, not assumed.** This repository records real numbers —
7.9MB to 37MB for `Microsoft.EntityFrameworkCore.Design`, 17MB to 50MB for Roslyn — so a Release
publish is measured before and after adding `Microsoft.Extensions.Caching.Hybrid`, and the number
goes into ADR 0009. It should be small, carrying no compiler, but "should be" is not the standard
here.

### Accepted risks

**The key space is unbounded in principle.** Any cursor string is a distinct key, so a client
looping over generated cursors can create entries freely. Bounded in practice by a TTL of at most
sixty seconds and by how deep anyone actually pages. If L1 growth ever becomes visible, the answer
is a `SizeLimit`-ed `MemoryCache` behind `HybridCache`; that is a follow-up, not part of this
design.

**A failed eviction is not caught.** With L1 only, `RemoveByTagAsync` has no realistic failure
mode, and catching it would mean `catch (Exception)` on a path that has no `IExceptionHandler`
parameter — banned everywhere outside the outbox's file-scoped exemption. So it propagates, and a
committed write would surface a 500 to a caller whose order was in fact saved. That becomes the
wrong trade the day an L2 tier is added, and it is recorded in ADR 0009 as the thing to revisit
then.

## Out of scope

**The frontend's query defaults.** `frontend/src/main.tsx` is a bare `new QueryClient()`:
`staleTime` is 0, so every mount and every window refocus refetches. That is a real problem and a
separate one. Fixing it here would put two unrelated designs behind one ADR.

**Redis and an L2 tier.** The `HybridCache` choice is what keeps this cheap to add later.

**Caching anything on the auth path.** Deliberately permanent, not deferred. ADR 0008's lockout
state must be read from the database every time.

## Files

```
new    src/Application/Abstractions/Caching.cs                ICacheable, IInvalidatesCache
new    src/Infrastructure/Caching/CacheOptions.cs
new    src/Infrastructure/Caching/CachingRegistration.cs      AddCaching()
new    src/Infrastructure/Caching/CacheDuration.cs            Clamp, pure and directly tested
new    src/Infrastructure/Messaging/CacheScope.cs
edit   src/Infrastructure/Messaging/Behaviors.cs              + CachedAsync, EvictAsync, Handle, QueryFailed
edit   src/Infrastructure/Messaging/MessagingRegistration.cs  AddQuery delegates, AddCommand evicts
edit   src/Infrastructure/InfrastructureRegistration.cs       + AddCaching()
edit   src/Infrastructure/AiFramework.Infrastructure.csproj   + Caching.Hybrid, pinned
edit   src/Application/Orders/GetOrder.cs                     + ICacheable
edit   src/Application/Orders/GetOrders.cs                    + ICacheable
edit   src/Application/Orders/PlaceOrder.cs                   + IInvalidatesCache
edit   src/Api/appsettings.json                               + Cache section
edit   frontend/playwright.config.ts                          webServer env: Cache__Enabled=false
edit   tests/Api.IntegrationTests/ApiFactory.cs               UseSetting Cache:Enabled=false
new    tests/Api.IntegrationTests/Orders/OrderCachingTests.cs
edit   tests/Infrastructure.Tests/Messaging/BehaviorTests.cs
edit   tests/Infrastructure.Tests/Messaging/RegistrationCompletenessTests.cs
new    docs/adr/0009-caching-scoped-to-the-caller.md
edit   CLAUDE.md                                              the three traps
edit   src/Application/CLAUDE.md                              how to opt a query in
edit   src/Infrastructure/CLAUDE.md                           where the behavior lives
edit   .claude/skills/dotnet-conventions/SKILL.md             the convention, loaded on write
```
