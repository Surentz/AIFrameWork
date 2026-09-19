# 0016. Realtime notifications over SignalR, with a Redis backplane

**Date:** 2026-09-19
**Status:** Accepted

## Context

The notification feed added with this feature is written by domain event handlers running off the
outbox: an aggregate raises an event, `DomainEventsInterceptor` persists it beside the aggregate,
and a background pump delivers it at least once afterwards. A client reads the result over REST
(`GET /api/notifications`, `GET /api/notifications/unread-count`).

That alone is a working feed, but only as fast as the client asks for it. A notification badge
that updates on the next poll is the difference between an application that feels live and one
that feels like a page you have to refresh — and the polling interval short enough to hide that
is the interval that costs the most.

The decision to add a realtime transport was taken deliberately, knowing it is the largest piece
of infrastructure in the feature. What makes it non-trivial here is not SignalR; it is this
repository's own deployment shape.

### Why one process is not the case to design for

`k8s/base/api.yaml` runs **two API replicas** — ADR 0010's whole point, to rehearse the things
that only break above one. Three facts then collide:

1. `OutboxPollerService` and `OutboxWorkerService` are `BackgroundService`s, so they run on
   **every** replica.
2. `OutboxPoller.ClaimAsync` claims rows with `FOR UPDATE SKIP LOCKED`, so **whichever** pod gets
   there first processes a given message. Nothing steers that to any particular pod.
3. A client's SignalR connection lives on exactly one pod — whichever terminated its handshake.

So the pod that writes a notification and the pod holding that user's connection are unrelated.
The ingress cookie affinity ADR 0010 relies on for cache correctness does **not** help: affinity
pins a user's *requests*, and the outbox pump is not serving the user's request. At two replicas
a naive `IHubContext.Clients.User(...)` call therefore reaches the intended person only when the
two coincide — roughly half the time — and does nothing at all the rest, with no exception, no
log, and a green test suite on a single-process test host.

That is precisely the failure mode this repository documents against everywhere else: silent,
environment-dependent, and invisible to the tests most likely to be run.

## Decision

**Push notifications over SignalR, with a Redis backplane, both gated behind
`Realtime:Enabled` which defaults to `false`. The REST feed remains the source of truth and push
is best-effort, permanently.**

- **`INotificationPush`** (`src/Application/Notifications/`) is the port. The SignalR
  implementation (`SignalRNotificationPush`) lives in **Api**, because `IHubContext<T>` is an HTTP
  transport concern that Application and Infrastructure must not reference. The port is what lets
  a domain event handler two layers down reach it without inverting the dependency rule.
- **It is injected as `IEnumerable<INotificationPush>`, never as a bare `INotificationPush`.**
  That is not stylistic. Domain event handlers must stay constructible from `AddInfrastructure`
  alone — `RegistrationCompletenessTests.AddInfrastructure_ResolvesAHandlerForEveryRegisteredDomainEvent`
  builds every handler chain to prove an event can never be dropped for want of a handler. A
  required dependency registered only in `Program.cs` would fail that test for a reason unrelated
  to what it guards. An empty sequence is a legitimate configuration meaning "no realtime
  transport wired up", and it needs no fake implementation that could be mistaken for a real one.
- **`Microsoft.AspNetCore.SignalR.StackExchangeRedis`** is the backplane, deployed as
  `k8s/base/redis.yaml` (a `Deployment` with no volume — see Consequences) and available locally
  behind a `realtime` compose profile, mirroring how Seq sits behind `observability`.
- **The push happens after the commit, never before.** `NotificationFanOut` saves the batch, then
  pushes. A client told about a notification whose transaction then failed would fetch the feed
  and not find it.
- **`SignalRNotificationPush` does not throw.** It runs on the outbox pump after the row is
  already durable, so a transport failure must not fail the message and redeliver work that
  already succeeded. It catches, logs at `Warning`, and returns — deliberately re-raising nothing
  except `OperationCanceledException`, which is the host shutting down and which
  `OutboxWorkItemProcessor` relies on propagating.
- **The hub has no methods.** Everything a client can *do* already has a REST endpoint that goes
  through the command/query pipeline and gets validation, logging and caching behavior. A hub
  method would be a second way in that bypasses all of it. The hub is push-only and `[Authorize]`d;
  without that attribute `UserIdentifier` is null and `Clients.User(...)` reaches nobody.

## Consequences

**Redis is now in the cluster, and it is the one component here that is disposable by
construction.** A backplane holds messages in flight, never state worth keeping — everything it
carries is already committed in Postgres. Hence a `Deployment` rather than a `StatefulSet`, no
PersistentVolumeClaim, no replication, and `--maxmemory 128mb --maxmemory-policy allkeys-lru`:
under pressure it drops the oldest in-flight messages, which costs live updates that clients
recover on their next read. An unbounded backplane is the one way this component could take a
node down with it.

**Realtime off is a first-class configuration, not a degraded one.** `Realtime:Enabled` defaults
to `false` everywhere — appsettings.json, every test host, CI — matching
`Observability:Otlp:Enabled`'s reasoning: the default must work with nothing else running. With
it off, no hub is mapped, no push is registered, and the feed behaves exactly as it does with it
on. `NotificationHubTests` pins both halves, including that the route is absent when disabled.

**Enabled with no backplane is legitimate at one replica and silently wrong above it.** A
developer's `dotnet run` is a single process, so push works with no Redis at all. Because that
same configuration is a real bug at two replicas, `Program.cs` logs a warning at startup rather
than letting it pass unremarked.

**A push is never delivery.** A disconnected client, a client on a replica that never heard about
the write, a dropped Redis message under `allkeys-lru` — all of them simply miss the update and
catch up on their next read. No caller may treat a successful push as delivery, and no caller may
skip writing the row because it pushed. This is stated on `INotificationPush` itself because it is
the assumption most likely to be quietly violated later.

**The e2e gate does not cover it.** `deploy/e2e-k8s.ps1` exercises two replicas, durable
Wolverine, caching and the rate limit; realtime push is not in that readiness path, for the same
reason `-WithObservability` is not — see ADR 0012. The cross-replica behaviour this ADR exists to
get right is therefore argued here and configured in `k8s/`, not proven by a test.

## Alternatives considered

**Postgres `LISTEN`/`NOTIFY` as a custom backplane.** Genuinely attractive: Postgres is already
in every environment, Npgsql supports it natively, and it would have added no new infrastructure
at all — the same instinct that produced a hand-built outbox rather than a broker. Rejected
because it means roughly 150 lines of custom backplane plus a listener `BackgroundService`, all
of it ours to test, own and get right, to replace a supported package that is five lines of
wiring. The outbox was worth building by hand because its semantics are the application's own;
a backplane's semantics are not.

**No backplane, with the client reconciling.** Push as explicitly best-effort, the client
refetching on reconnect. Least code, and the reconciliation is needed anyway. Rejected as the
*primary* mechanism because "realtime that works about half the time" is exactly the
environment-dependent silence this codebase spends so much effort eliminating — and because the
failure is invisible on a single-process test host, so nothing would catch a regression.

**Server-Sent Events instead of SignalR.** One-way push is all this feature needs, and SSE is
simpler with no hub, no negotiate and no protocol handshake. Rejected because it solves the
transport and leaves the actual problem — fan-out across replicas — entirely unsolved: an SSE
endpoint has the same pod-affinity gap and would need the same backplane, hand-rolled.

**Pushing from the command handler instead of the outbox handler.** `ShipOrder` knows the
recipient and runs on the pod serving that user's request, where cookie affinity means their
connection probably *is* local. Rejected because it only works for the notifications a user
causes themselves: a price change notifies other people, and nothing about it runs on their pods.
It would also put the push before the outbox delivery that writes the row, inverting the
commit-then-push ordering above.

**Making the feed `ICacheable` to reduce polling instead.** Rejected on its own terms and
recorded on `GetNotifications`: eviction is scoped to the calling user, and nothing a caller does
creates their own notifications, so a cached feed could never be evicted — it would simply go
stale for the whole TTL, and contradict any push that did arrive.
