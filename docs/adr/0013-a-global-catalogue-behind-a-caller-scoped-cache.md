# 0013. A global catalogue behind a caller-scoped cache

**Date:** 2026-09-12
**Status:** Accepted

## Context

Every persisted aggregate in this codebase so far belongs to somebody. `Order` carries a `UserId`
and ADR 0007 pushes that filter into SQL, so a caller cannot read another caller's row even by
mistake. `User` is its own owner. The caching design that grew on top of those, ADR 0009, took
the same shape and made it structural: `CacheScope` prepends `ICurrentUser.Id` to every key and
to every tag, `Behaviors.CachedAsync` throws rather than cache an `ICacheable` query dispatched
with no caller, and `Application/CLAUDE.md` tells you in as many words never to put a user id in
`CacheKey` because one is already there.

That is a deliberate, load-bearing asymmetry: a cache key that omits the user is a data leak
rather than a staleness bug, so the pipeline refuses to let anyone write one.

A product catalogue is the first thing in this repository that is genuinely **global**. One list,
the same for everybody, and — per the decision taken with this feature — writable by any signed-in
caller, because there is no role system yet (`User.SecurityStamp`'s comment still calls
permissions a "plan 2" idea). So `GetProducts` and `GetProduct` want caching for exactly the
reason `GetOrders` does, but they do not want the scoping that comes welded to it.

The collision is in eviction, not in reads. `Behaviors.EvictAsync` removes by
`CacheScope.Tag(tag, userId)` — the writer's own tag. When user A adds or edits a product:

- A's cached catalogue pages are evicted, so A reads their own write immediately.
- B's, C's and everyone else's are not touched. They keep serving the pre-write catalogue until
  their entries expire on `HybridCache`'s own clock.

And ADR 0010 already established that `HybridCache` here is L1-only, so even the writer's eviction
reaches only the pod that handled the write — which is why the ingress pins cookie affinity.

## Decision

**Cache the catalogue reads at thirty seconds, keep the caller scoping exactly as it is, and treat
the resulting cross-caller staleness as a bounded, documented cost rather than a defect.**

`GetProducts` and `GetProduct` implement `ICacheable` with `Duration => TimeSpan.FromSeconds(30)`,
the same as their orders counterparts. `CreateProduct` and `UpdateProduct` implement
`IInvalidatesCache` with `Tags => [nameof(GetProducts), nameof(GetProduct)]`, which buys
read-your-own-writes for the caller doing the writing and nothing more.

The ceiling on how long anyone else sees a stale catalogue is therefore the TTL — thirty seconds —
not the eviction. That is written down in `GetProducts`'s own comment, next to the `Duration`, so
the next person to change that number knows what they are changing.

The endpoints stay `[Authorize]` even though the data is not per-user. Two reasons, and the second
is mechanical: the catalogue is not public, and an `ICacheable` query dispatched with no caller
throws by design.

## Consequences

**The TTL is now the only lever.** Shortening `Duration` is how you make edits propagate faster;
there is nothing else to turn. Thirty seconds was chosen to match `GetOrders` rather than because
a catalogue needs exactly that.

**Two callers hold two copies of identical data.** The cache is scoped per user, so N active users
means up to N entries for one global list. That is wasted memory in exchange for one invariant
that never has to be reasoned about per query: no entry is reachable by a caller other than the
one it was cached for.

**Eviction reads as more protection than it gives.** `CreateProduct`'s tags look like "this
invalidates the catalogue" and mean "this invalidates the catalogue *for me*". Both command
records carry a comment saying so; without it the next reader will reasonably assume a write
refreshes the list for everybody.

**A role system will not change any of this.** Restricting writes to administrators shrinks the
number of people who can cause staleness; it does not change who sees it.

## Alternatives considered

**An unscoped cache path for queries that declare themselves global** — a second marker, say
`IGloballyCacheable`, whose keys and tags omit the user. Rejected, and this is the important one.
The scoping is not an inconvenience wrapped around the cache; it is the property that makes the
cache safe to use without thinking. A second path means `CacheScope` is no longer the single
source of key composition — the thing `Infrastructure/CLAUDE.md` says must stay true, because the
tag being the key's own prefix is the whole eviction mechanism — and it means every future
`ICacheable` query is a decision about which marker to use, with a data leak as the cost of
getting it wrong. Trading a silent-leak failure mode for a thirty-second staleness one is not a
close call.

**No caching for the catalogue at all.** Honest, and genuinely tempting: nothing here is slow, and
uncached reads are never stale. Rejected because the catalogue is the read most likely to be
requested by everybody at once — it is the thing the app lists — so it is the worst candidate to
single out as the one uncached read, and because the framework's own conventions exist to be
followed by new features rather than opted out of.

**Evicting across callers by tracking who has cached what.** Rejected outright at this size. It
means a registry of live cache entries per query type, kept correct across eviction and expiry,
to save at most thirty seconds of staleness on data that is not changing quickly. And it would
still stop at the pod boundary, because L1-only is ADR 0010's constraint, not this one's.

**A shorter TTL for the catalogue than for orders** — five seconds, say, on the grounds that a
global list has more readers to disappoint. Rejected for now because it is a number with no
evidence behind it, and an inconsistency between two otherwise identical queries invites the
question "why" at every future read. Thirty seconds everywhere is easier to defend until
something measured says otherwise.
