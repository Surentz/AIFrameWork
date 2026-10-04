---
name: notifications
description: Use when touching the in-app notification feed, a domain event handler that writes notifications, or realtime push over SignalR - saving from an outbox handler, idempotency, enums on the wire, Realtime__Enabled and the Redis backplane.
---

# Notifications

An in-app feed, one row per recipient, written by domain event handlers off the outbox and read
over REST (`/api/notifications`, `/api/notifications/unread-count`). Five kinds:
`OrderPlaced`, `OrderShipped`, `OrderCancelled`, `ProductPriceChanged`, `OrderExportReady`. The
first three notify the buyer; `ProductPriceChanged` notifies everyone who has previously ordered
that product; `OrderExportReady` tells an export's owner it can be downloaded. That last one is
raised in the worker and delivered by an API replica, which is exactly the path ADR 0028 exists
for; its `SubjectId` is the export, and the frontend links it to `/orders/exports` (ADR 0029).

Five things that will cost you time:

- **A domain event handler must save its own work.** It runs on the outbox pump, *not* through
  the command pipeline, so the unit-of-work behavior that commits exactly once after a command
  never runs for it. `NotificationFanOut` calls `IUnitOfWork.SaveChangesAsync` explicitly; without
  it, every notification is added to a `DbContext` that is disposed with the scope and nothing is
  written — silently, with the outbox row still marked `Processed`. `OrderAuditWriter` sidesteps
  the question by issuing an immediate `INSERT` instead of tracking an entity.
- **`GetAsync` reads untracked; `GetForUpdateAsync` tracks.** Two methods rather than a bool,
  because the distinction decides whether a write happens at all and a caller that gets it wrong
  gets no feedback. `ShipOrder`, `CancelOrder` and `MarkNotificationRead` all mutate what they
  read, so all three take a tracked read — `ShipOrder`'s is `GetForFulfilmentAsync`, the
  cross-owner one, since the operator ships (ADR 0024).
- **Idempotency is the unique index, not the check.** `(SourceMessageId, UserId)` is unique;
  `ListNotifiedRecipientsAsync` is an optimization in front of it. Delivery is at-least-once, so
  every notifier WILL run twice — two concurrent deliveries can both pass the check, and the index
  is what stops a duplicate landing in someone's feed.
- **The feed is deliberately NOT `ICacheable`**, permanently, for two independent reasons. Nothing
  a caller does creates their own notifications — the outbox pump does, with no current user — so
  there is no command to hang an `IInvalidatesCache` tag off and a cached feed could never be
  evicted. And a cached feed would contradict the realtime push. Same posture as the auth path.
- **Enums cross the wire as names**, via a `JsonStringEnumConverter` registered on **both**
  `AddJsonOptions` (what controllers serialize with) and `ConfigureHttpJsonOptions` (what
  `AddOpenApi`'s schema generator reads). Configuring only the first is the trap: the API sends
  `"OrderPlaced"` while the generated contract still says `type: integer`, so `schema.d.ts` types
  it `number` and every client is wrong in a way no backend test can see.

Realtime push is opt-in (`Realtime__Enabled`, default `false`) and **best-effort by contract** —
the feed is the truth. Above one replica it needs the Redis backplane, because the pod that writes
a notification is whichever one's outbox pump claimed the row and is unrelated to the pod holding
that user's connection; the ingress cookie affinity of ADR 0010 does not help, since the pump is
not serving that user's request. See ADR 0019.

**Only the API delivers the outbox, because only the API can push.** `SignalRNotificationPush` is
registered in `src/Api/Program.cs`, so a notifier running anywhere else writes the row and pushes
to nobody. The pumps therefore come from `AddOutboxPumps()`, which only the API calls; the worker
writes outbox rows but never claims them. Until 2026-10-02 it did, and every event it claimed was
a silent 30-second-stale badge. If a new host ever wants to run the pumps, it needs a push path
first. See ADR 0028.

**It is ON in two places, for two different reasons.** In Development
(`src/Api/appsettings.Development.json`) with **no** backplane: a developer's `dotnet run` is a
single process, the one configuration where push cannot reach the wrong replica. And in the
Kubernetes overlay (`k8s/overlays/local/config.yaml`) **with** one —
`Realtime__RedisConnectionString: 'redis:6379'`, against the Redis that `k8s/base/redis.yaml`
deploys for exactly this purpose, because that cluster runs two API replicas and a push must
reach a user whose connection is held by the other pod.

It stays **off** everywhere else: `appsettings.json`'s default, `ApiFactory`, and the Playwright
`webServer` — the last pinned explicitly, since the e2e run sets
`ASPNETCORE_ENVIRONMENT=Development` and would otherwise inherit the dev value.

**Without push the badge is up to thirty seconds stale, by construction.** The outbox pump polls
every second, so the notification row exists almost immediately; `useUnreadCount`'s interval is
the only other thing that would ever ask. That gap is what `frontend/src/features/notifications/
stream.ts` closes — and a `ProductPriceChanged` aimed at someone who took no action has no other
trigger at all, since no client-side invalidation can fire on a user who did nothing. The
frontend needs `/hubs` proxied for any of it to work in dev; see `frontend/CLAUDE.md`.

