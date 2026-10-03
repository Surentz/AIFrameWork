# 0028. The outbox is delivered only where the push transport lives

**Date:** 2026-10-02
**Status:** Accepted

## Context

`AddInfrastructure` called `AddOutbox()`, and `AddOutbox()` registered the two outbox pumps
(`OutboxPollerService`, `OutboxWorkerService`). Every host that composes Infrastructure therefore
delivered domain events — once the worker existed (ADR 0016), that meant the worker as well as
every API replica. `src/Worker/CLAUDE.md` recorded it as a side effect ("a third poller, not a
replacement") and called it safe, because `OutboxPoller.ClaimAsync` claims with
`FOR UPDATE SKIP LOCKED`.

It was safe for the outbox and wrong for the notifications. The pumps run every
`IDomainEventHandler`, and the notifiers are among them: each writes a feed row and then hands it
to `IEnumerable<INotificationPush>` (ADR 0019). The only implementation, `SignalRNotificationPush`,
needs `IHubContext<NotificationHub>`, so it is registered only in `src/Api/Program.cs` — the worker
cannot register it without referencing the Api, which ADR 0016 forbids. In the worker the sequence
is empty, which `INotificationPush` documents as "no realtime transport". So whenever the worker
won the claim, the notification was written and nobody was pushed, and the user's badge caught up
on its next poll, up to thirty seconds later. Every existing kind was affected, in dev (one API, one
worker) and in the cluster (two APIs behind the Redis backplane, one worker) alike. No test caught
it: realtime is off under test, and the worker's own tests asserted only that the outbox drained.

The gap surfaced while designing a feature whose whole point is "the worker finishes, the user is
told" (an order export built by a job). That feature would have made it visible, so it is fixed
first.

## Decision

The pumps are registered by a separate `AddOutboxPumps()`, which only the Api's `Program.cs` calls.
`AddOutbox()` keeps everything else — options, channel, `OutboxPoller`, `OutboxWorkItemProcessor` —
so every host can still write outbox rows (the interceptor is unchanged) and a test can still drain
them by hand.

The rule is stated in terms of the reason, not the host: **the outbox is delivered only by a host
that has the push transport.** Today that is the Api. The worker writes outbox rows — a
warehouse's `RecordShipment` raises `OrderShipped` there — and an Api replica delivers them.

## Consequences

- Every notification is written by a process that can push it. With one API replica that is
  enough on its own; with more, the Redis backplane (ADR 0019) carries the push to whichever
  replica holds the connection, as it already did.
- It is enforced by code rather than configuration, the same posture as `ICurrentUser` and
  `IClientContext` being registered per host: there is no setting to flip back by accident.
  `OutboxRegistrationTests.AddOutbox_Alone_StartsNoPump` pins that Infrastructure alone starts
  none, `OutboxPumpTests.TheWorker_RunsNoOutboxPump` pins the worker, and `ApiFactory` refuses to
  build a host without both pumps.
- **The worker loses its share of outbox capacity.** Delivery is back to the API replicas' pumps
  alone, which was ADR 0016's original capacity. Nothing measured needed more.
- **A domain event the worker raises waits for an API replica.** Latency is one API poll interval
  (one second); if no API is running, nothing drains until one starts. That is the same
  dependency an API-raised event already had.
- The incidental benefit `src/Worker/CLAUDE.md` noted — an event that enqueues a job being
  delivered by the process that will run the job — is gone. It was never relied on: the job goes
  through RabbitMQ either way.
- **It constrains ADR 0016's follow-on.** Moving the pumps *off* the API was recorded as the open
  next step. That now needs a push path that works from any process first (below); moving the
  pumps without one would silence realtime push entirely rather than a share of it.
- Worker integration tests that relied on the worker's own pump now drain explicitly
  (`WorkerFactory.DrainOutboxUntilEmptyAsync`, the same loop as `ApiFactory`'s).

## Alternatives considered

**A push relay through Postgres `LISTEN`/`NOTIFY`.** Whoever writes a notification sends a
`NOTIFY` carrying the user and notification id after the commit; every API replica `LISTEN`s and
pushes to the connections it holds. Push then works from any process, and the Redis backplane
becomes unnecessary. It lost on size, not merit: a long-lived listener connection per replica with
its own reconnect handling, and a rewrite of part of ADR 0019, to fix a bug that one registration
split fixes. **It is the precondition for moving the pumps off the API**, and the place to start
when that is wanted.

**A SignalR backplane client in the worker.** Publishing through Redis from the worker needs the
hub type, which lives in the Api and cannot be referenced from the worker (ADR 0016), and dev runs
no Redis at all, so it would still fail there.

**Configuration (`Outbox__Enabled=false` on the worker).** The same effect with a switch that can
be flipped back, and that a new host would inherit as "on" by default. The reason is structural
— which host has the transport — so the code says it.

**Leave it, and have clients poll faster.** Push exists precisely so a user who did nothing (a
`ProductPriceChanged` aimed at a past buyer) is told; a shorter poll interval moves load onto every
open tab to paper over a registration mistake.
