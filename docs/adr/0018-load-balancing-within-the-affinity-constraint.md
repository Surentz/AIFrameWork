# 0018. Load balancing within the affinity constraint

**Date:** 2026-09-20
**Status:** Accepted

## Context

The deployment has run at two API replicas since ADR 0010, behind ingress-nginx. That reads like
load balancing and is only half of it: the `aiframework-api` Ingress pins each user to a pod with
a cookie for an hour, so traffic is balanced at the granularity of a **whole user session**, not
of a request.

That pinning is not decoration. `HybridCache` is L1-only, so without it a `PlaceOrder` handled by
pod B cannot evict pod A's cached order list, and the user's own new order disappears from their
list for up to the 30-second TTL — measured at roughly one placement in four. ADR 0010 chose
affinity over a Redis L2 tier and left one question open: whether `HybridCache`'s tag-based
eviction works against an L2 tier on .NET 10.

**That question has now been answered, and the answer is no.** ADR 0010's 2026-09-20 amendment
records the spike in full. The short version: the L2 tier *is* shared across pods and
`RemoveByTagAsync` *does* publish a marker to Redis, but a pod that has already observed a tag
keeps its own view of that tag's invalidation state and serves the stale entry for the life of
the entry — not eventually consistent, not fixable with `DisableLocalCache`, and with no option
on `HybridCacheOptions` governing it.

So the affinity stays, and the question this ADR answers is narrower than "how do we load
balance": it is **what load balancing is worth having while sessions are pinned.**

What the pinning costs, stated plainly, because these are the things an autoscaler cannot fix:

- Two users of very different weight can hash to the same pod and stay there for an hour.
- Scaling out does not rebalance anyone already connected. A new pod receives only sessions that
  begin after it does.
- Losing a pod moves its entire user population at once rather than bleeding off.
- The auth rate limit remains N × `PermitLimit`, since partitioning is per-process (ADR 0010).

And one thing that was wrong independently of affinity: `deploy/kind-cluster.yaml` declared a
**single node**. Two replicas on one node is one failure domain wearing a replica count — the
`kubectl drain` that ADR 0010 exists to rehearse would have taken both pods together.

## Decision

**Give the kind cluster two worker nodes.** Without them nothing else in this ADR means
anything: a disruption budget that keeps one pod alive is no protection when both pods share a
kubelet. kind taints the control-plane node once workers exist, so application workloads move
onto the workers while ingress-nginx stays put via its `ingress-ready` nodeSelector and its
control-plane tolerations. Two rather than three, because the control-plane is already carrying
the ingress and each extra node is another full kubelet on a developer's machine.

**Spread the API replicas across nodes with `whenUnsatisfiable: DoNotSchedule`.** The soft
variant would silently degrade to co-located pods under the smallest scheduling pressure — which
is the exact state this is meant to prevent and the exact state nobody would notice. It stays
satisfiable as the autoscaler scales: `maxSkew: 1` across two worker nodes spreads six replicas
three and three. `web` uses `ScheduleAnyway` instead, because serving the static bundle from a
co-located pod beats not serving it.

**Autoscale `api` and `web` on CPU, and `api` on memory as well.** The API's pressure is not only
compute: `HybridCache`'s L1 and the EF change tracker both live in the heap, and the 768Mi limit
turns memory exhaustion into an OOMKill rather than a slow pod. `web` is nginx serving a static
bundle, so its memory is flat by construction and a memory target would be a metric that never
moves.

`minReplicas: 2` is a floor, not a starting point. Scaling to one under light load would quietly
stop rehearsing every multi-instance failure this cluster exists to surface, and the first anyone
would know is when production met one.

The scale-down stabilization window is **five minutes**, well above the cache's 30-second TTL.
Removing a pod evicts every session pinned to it, and under affinity those users land on a pod
holding nothing for them — a cold miss and a database read each. Scaling down eagerly would turn
ordinary traffic variation into repeated stampedes. Scale-up uses 60s rather than the 0s default,
so a single spiky scrape does not produce pods that are unnecessary by the time their
deliberately-slow startup probe passes.

**metrics-server is installed by `deploy.ps1`, not left as a prerequisite.** Without it every HPA
reports `unknown` for its metric and never scales — and reports it as a condition on the HPA
rather than as a failure of the deploy, so the cluster looks healthy while the autoscalers do
nothing. kind's kubelets serve metrics with a certificate metrics-server does not trust, so the
install is patched with `--kubelet-insecure-tls`; that is the documented answer for local
clusters and is not a pattern for an overlay targeting a real environment.

**Disruption budgets for all three workloads, and the worker's is deliberately different.**
`api` and `web` get `minAvailable: 1` rather than a `maxUnavailable` percentage, because with an
HPA the replica count moves and a percentage is a fraction of a number that changes underneath
it. The worker gets `maxUnavailable: 1`: it runs at a single replica, and `minAvailable: 1` on a
one-replica Deployment blocks a node drain **indefinitely**, since the budget can never be
satisfied while the only pod is being evicted. That is correct for the worker specifically —
jobs are durable in Postgres and redelivered, so a restart costs latency rather than work.

**The worker gets no autoscaler.** Its work arrives through Wolverine queues off Postgres, so
what says whether it is keeping up is queue depth, not CPU — a worker blocked on a slow query
sits at low CPU while falling further behind, and a CPU-driven HPA would scale it *down* exactly
then. Nothing about the worker forbids more replicas: `OutboxPoller` claims rows with
`FOR UPDATE SKIP LOCKED` and Quartz runs clustered (ADR 0017), so a second worker is safe by
construction. The gap is the trigger, not the safety.

## Consequences

**The honest summary: this buys headroom and survivability, not evenness.** New traffic gets more
pods to land on, a node drain no longer takes the deployment with it, and two replicas finally
means two failure domains. None of it makes an existing hot session move. Anyone expecting
request-level balancing should read ADR 0010's amendment first.

`api.yaml` and `web.yaml` still declare `replicas: 2` alongside their autoscalers. The usual
advice is to drop the field so `kubectl apply` and the HPA do not fight over it; here the
manifest value *is* the HPA's `minReplicas`, so a deploy resetting it means returning to the
floor the autoscaler enforces anyway, and a deploy recreates every pod regardless. Keeping it
leaves "two replicas is the point of this cluster" stated where a reader of the Deployment sees
it.

### Accepted trade-offs

**The cluster is heavier.** Two more kind nodes is two more kubelets, container runtimes and sets
of system pods on the developer's machine — roughly a gigabyte before any application pod
starts. The observability profile was already the largest thing here and now sits on top of a
larger floor.

**Postgres is pinned to whichever node it first scheduled on.** Its `volumeClaimTemplates` use
kind's default node-local storage class, so the volume binds to one node and the pod cannot move.
This was invisible at one node and is now a real property: draining the node Postgres is on will
not reschedule it. That is a faithful rehearsal of node-local storage rather than a defect, but a
drain test should target a node that is not hosting it.

**None of this has been verified against a running cluster.** The manifests render through
`kubectl kustomize` and validate against Kubernetes 1.34 schemas with `kubeconform --strict` —
51 resources across both overlays, zero invalid — but no cluster was reachable where they were
written, so no HPA has been observed scaling, no drain has been observed respecting a budget, and
the metrics-server patch has not been observed taking effect. The first `deploy.ps1
-CreateCluster` run is the real test, and the things most likely to need adjustment are the
metrics-server patch path and whether `DoNotSchedule` leaves pods pending at the top of the
api HPA's range.

**Scaling on the right signal is still missing for the worker**, and queue-depth metrics are the
prerequisite. Until then its replica count is an explicit decision in `worker.yaml`.

## Alternatives considered

**A Redis L2 tier, to remove the affinity and get request-level balancing.** Spiked and rejected
on evidence; see ADR 0010's amendment.

**A per-user cache generation counter in Redis, composed into the cache key.** This sidesteps tag
invalidation entirely — eviction becomes "the old key is never requested again", which crosses
pods correctly because the generation is read from Redis rather than inferred from per-process
state. It is the remaining candidate for removing the affinity, at the cost of one Redis round
trip per cached read and a real change to `CacheScope` and `Behaviors.cs`. Not taken here because
it has not been spiked and it is a caching decision rather than a load-balancing one; it would
need its own ADR.

**Moving the rate limit to the ingress**, so it sees every replica's traffic as one stream. ADR
0010 already names this as the exit if the N × `PermitLimit` loss ever matters. It still does not,
and autoscaling does not change that: the per-account lockout of ADR 0008 is enforced against
Postgres on every sign-in regardless of pod count.

**A `VerticalPodAutoscaler`** instead of, or beside, the HPA. Rejected: it fights an HPA on the
same resource metrics unless carefully partitioned, and the request values here were set
deliberately in ADR 0010's sizing rather than discovered.
