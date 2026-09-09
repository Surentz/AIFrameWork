# 0010. Running on Kubernetes: shared state across two replicas

**Date:** 2026-09-09
**Status:** Accepted

## Context

The goal is a local Kubernetes cluster that runs the whole application — API, SPA, and
Postgres — with the API on **two replicas**, not one. The point of two is not scale; it is
that this repository has never rehearsed a deployment where the application talks to more
than one instance of itself, and some failures only exist at that count. Running at two
replicas locally, in a kind cluster, is what makes those failures reachable somewhere cheap
instead of somewhere that pages someone.

The application holds three pieces of state in memory, each invisible at one replica and each
a decision rather than a discovery at two:

| Component | Behaviour at two replicas |
|---|---|
| Cookie auth's Data Protection key ring | **Breaks.** No key persistence is registered, so each pod mints its own ephemeral ring and rejects cookies issued by the other. Users see intermittent 401s, and a single pod restart signs everyone out. |
| `HybridCache` eviction | Caching itself keeps working. Cross-pod invalidation does not: a write handled by one pod cannot evict an entry held by another. |
| The auth rate limiter (ADR 0008) | Keeps working per pod. The effective limit across the deployment becomes N × `PermitLimit`, because partitioning is per-process. |

The outbox needs no decision here: `OutboxPoller.ClaimAsync` claims due rows with
`FOR UPDATE SKIP LOCKED`, so concurrent pollers across pods were already safe by
construction.

The full design, including the manifests, the images, and the deploy flow, is recorded at
`docs/superpowers/specs/2026-09-09-kubernetes-deployment-design.md`. This ADR condenses the
decisions that change an existing assumption in the running application, and is the durable
record `src/Infrastructure/AiFramework.Infrastructure.csproj` and `src/Api/Program.cs` already
point at.

## Decision

**The Data Protection key ring moves to Postgres, and this is mandatory, not a preference.**
With no persistence configured, ASP.NET Core falls back to a per-machine filesystem ring,
which does not exist across containers — each pod generates its own. Skipping this produces a
bug that looks like a flaky login and is miserable to diagnose, because it is intermittent by
construction: a request lands on the pod that minted the cookie and succeeds, or on the other
pod and gets a 401, and which one happens is invisible to the person hitting it.

```csharp
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AiFrameworkDbContext>()
    .SetApplicationName("AiFramework");
```

`AiFrameworkDbContext` implements `IDataProtectionKeyContext`, alongside the outbox and order
audit tables it already owns, and the key table arrived as its own append-only migration.
`SetApplicationName` is load-bearing, not decoration: the key ring's purpose string is derived
from it, so two pods that disagree on the application name read the same table and *still*
refuse each other's cookies — the same symptom, with the fix apparently already applied. This
shipped in commit `84650ad`, ahead of this ADR, with an integration test that issues a cookie
from one `WebApplicationFactory` host and asserts a second host, built over the same Postgres
container, accepts it — the only thing that actually guards the regression; asserting the key
table exists proves nothing.

**Cache eviction correctness now rests on ingress cookie session affinity, and this amends
ADR 0009's single-process premise.** ADR 0009 designed the query cache and its synchronous,
in-request eviction on the assumption of one process. `Behaviors.cs` says so out loud, at the
point that assumption is spent:

> With an L1-only HybridCache RemoveByTagAsync has no realistic failure mode.

At two replicas that stops being true, and the failure is specific enough to write out exactly:

1. `GET /api/orders` lands on pod A, misses, and pod A caches `GetOrders:{userId}` for 30s.
2. `POST /api/orders` lands on pod B, commits, and `EvictAsync` calls `RemoveByTagAsync`
   against **B's** memory. Pod A's entry is untouched — pod B has no way to reach it.
3. The frontend's immediate refetch, milliseconds later, lands on pod A again: a cache hit,
   and a stale one. The order the user just placed is missing from the list for up to the
   30-second TTL.

That is precisely the read-your-own-writes guarantee ADR 0009's synchronous eviction path was
built to provide, and with the ingress pooling and spreading connections across two pods, step
1 and step 3 landing on different pods is close to a coin flip rather than a rare interleaving.

The resolution is ingress session affinity, not Redis:

```yaml
nginx.ingress.kubernetes.io/affinity: "cookie"
nginx.ingress.kubernetes.io/session-cookie-name: "aiframework.route"
```

Pinning a user to one pod makes the pod that evicts the same pod that reads, which restores
the guarantee without adding a stateful dependency. What survives the amendment intact is
ADR 0009's own per-caller key scoping: `CacheScope` composes every key and tag with
`ICurrentUser.Id`, so the blast radius of this gap was already, and remains, strictly the
acting user seeing their own stale view. There is no cross-user leakage — the property ADR
0009 was most protective of is unaffected by moving to two processes.

The residual risk under affinity is smaller than it first looks: if a pod dies and a user is
rebalanced, the new pod holds no entry for them at all, because affinity meant they were never
served there. That is a cold miss and a database read, not a stale read. A stale read would
need two rebalances inside the same 30-second window.

**Kustomize over Helm, with the migration-ordering cost moved into `deploy/deploy.ps1`.**
There is exactly one environment here, so Helm's templating buys nothing. What Helm did offer
was its `pre-upgrade` hook, the natural primitive for "migrations must finish before any pod
starts." Kustomize has no hook concept at all, so that ordering becomes an explicit script
rather than a manifest feature — `deploy/deploy.ps1` (PowerShell, matching the existing
`.claude/hooks` and the development platform) applies the base resources, waits for Postgres,
deletes and reapplies the migration Job, waits for it to complete, and only then applies `api`,
`web`, and the ingress. That script, five steps long, is the acknowledged price of choosing
Kustomize.

**Postgres only for shared state. No Redis.** The key ring and the eviction-affinity decisions
above both keep the deployment's only stateful dependency at Postgres, which is already
running and already backed by a fixture. A Redis L2 tier for `HybridCache` is the principled
exit from the affinity dependency, and it is deliberately not taken here: `HybridCache`'s
tag-based eviction — the exact mechanism ADR 0009's invalidation relies on — had incomplete
support against an L2 tier in .NET 9, and its .NET 10 status has not been verified. Adding
Redis on an eviction mechanism that has not been confirmed to work against it would trade a
dependency the design can already reason about for one it cannot yet.

## Consequences

**This resolves the two comments already shipped ahead of this ADR.**
`AiFramework.Infrastructure.csproj` documents the Data Protection package with "See ADR 0010,"
and `Program.cs` documents the key ring registration with "the condition ADR 0010 places on a
cloud target" — both point here, and both are now backed by a decision rather than a forward
reference.

### Accepted trade-offs

**The auth rate limiter's effective ceiling becomes N × `PermitLimit`.** ADR 0008's limiter
partitions by client IP address in a per-process fixed window; at two replicas, a client's
requests are split roughly in half across two independent windows, so the effective limit
doubles. This is a volume-defence loss, not a correctness one: ADR 0008's per-account lockout
is enforced against Postgres and checked on every sign-in regardless of which pod handles it,
so the layer actually defending an individual account is untouched. The exit, if the volume
loss ever matters, is moving the limit to the ingress, where it would see every replica's
traffic as one stream; that move is deliberately not made now.

**Data Protection keys are stored unencrypted at rest.** `PersistKeysToDbContext` with no
`ProtectKeysWith*` configured stores the key ring in Postgres in plain form. This is the same
judgement already applied to the committed dev connection string in
`src/Api/appsettings.Development.json`: acceptable for a localhost-only cluster running
throwaway credentials that are never deployed. It is recorded here as a condition, not an
aside — a cloud target requires `ProtectKeysWith*` before this decision can be reused there,
and `Program.cs`'s own comment carries that same condition forward to the next reader of the
registration.

## Alternatives considered

The rejected shapes for the two central decisions are argued where those decisions are made,
above: Helm, for the manifests; Redis, both as the general shared-state store and as the
specific exit from the affinity dependency for cache eviction. Two more were on the table for
the cache-eviction problem specifically and are named here for the record, without further
argument beyond what is above: disabling the cache outright, and a Postgres
`LISTEN`/`NOTIFY`-driven cross-pod eviction channel.

A single API replica was rejected outright, for the reason given in Context: it would leave
every failure this ADR records undiscovered until a real multi-instance environment hit it
first.

See also ADR 0009, which this ADR amends the single-process premise of, and ADR 0008, whose
per-account lockout is what keeps the rate-limiter trade-off above a volume issue rather than
a correctness one.
