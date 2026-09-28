# 0026. RabbitMQ is the broker for all asynchronous work

**Date:** 2026-09-28
**Status:** Accepted

**Supersedes** ADR 0016's "The transport stays PostgreSQL", and the deferral of RabbitMQ it
records — and only that. Jobs still run in the worker, the API still listens to no queue, and the
lanes, the retry policy and Quartz (ADR 0017) all stand. ADR 0005's no-broker stance goes with it;
its durable event path on PostgreSQL does not.

## Context

The owner asked for the system flow to be *UI → API → queue → DB, and back again*, with RabbitMQ
as the queue and Wolverine kept as the messaging layer. There is no RabbitMQ server yet, so it
runs as a container everywhere this repo runs: the dev loop, the e2e stack, the kind cluster and
the integration tests.

ADR 0016 had deferred RabbitMQ behind four named triggers. The fourth — **a consumer outside this
solution** — now holds twice over: other systems are to receive this application's events
(orders placed, shipped and cancelled; price changes), and a warehouse is to send it shipment
confirmations. Neither is a job, and PostgreSQL queues are not something an outside system can
reasonably publish to or subscribe on. A broker was also the one piece ADR 0016 had already
confirmed to be a registration change against the installed Wolverine package.

What the request could *not* mean, taken literally, is a queue in front of every write. Every
endpoint would answer `202 Accepted`, read-your-own-writes would break, every screen would need a
pending state, and sign-in and the per-request security-stamp read (ADRs 0011, 0020) could not go
through a queue at all.

Before any of this was built, the parts the design leaned on were verified against a real broker
and the installed WolverineFx 6.40.0 / RabbitMQ.Client 7 packages rather than taken from the
docs: the plan's "Verified API" table
(`docs/superpowers/plans/2026-09-27-rabbitmq-messaging.md`, Task 1) records each answer, and
several of them changed the design. This ADR states what was verified, not what was assumed.

## Decision

**Requests stay synchronous; everything asynchronous goes through RabbitMQ.** UI → API → DB is
still the path for anything a user waits on. Background jobs (API → RabbitMQ → worker → DB),
events for other systems (DB → outbox → RabbitMQ → out) and messages from other systems (in →
RabbitMQ → worker → DB) all ride the broker. "Back again" to the UI is the existing notification
feed and SignalR push. Wolverine is the messaging layer in both hosts; Application code never
references Wolverine or RabbitMQ.

**Topology.** Everything is durable and declared by Wolverine at startup (`AutoProvision()`), all
names in `RabbitMqTopology`: two quorum queues for the job lanes (`aiframework.jobs.light`,
`aiframework.jobs.heavy`); a topic exchange `aiframework.events` for everything we publish, routed
by the domain event's registered name (`order.placed`, …); an alternate exchange and quorum queue
`aiframework.events.unrouted`, capped at 100,000 messages with `drop-head`, so an event no consumer
has bound for is kept rather than silently dropped; and a quorum queue `aiframework.shipments` for
inbound confirmations. Consumers declare and bind their own queues, so adding one never touches
this application. Both hosts call `DisableSystemRequestReplyQueueDeclaration()`: without it
Wolverine declares a classic `wolverine.response.<guid>` queue and **listens** on it even on a
sender-only connection (verified), which would break both "quorum everywhere" and "the API listens
to no queue".

**Contracts.** Plain System.Text.Json bodies, camelCase, enums as names, in versioned records under
`src/Application/IntegrationEvents` — separate from the domain events, so a domain event can change
shape without breaking anyone outside. Every message carries standard AMQP properties:
`message_id` = `eventId`, `type` = the versioned name (`order.placed.v1`), `content_type` =
`application/json`, `correlation_id` = the originating request's trace id (verified by
`OutboundEventTests`). `eventId` is the hand-built outbox's `MessageId`, stable across redelivery.
Fields may be added within a version; renaming or removing one is a `.v2` published alongside
`.v1` until consumers move.

**Outbound flow.** A new kind of domain-event handler, one integration publisher per event, maps
the domain event to its contract and calls the `IIntegrationEventPublisher` port.
`WolverineIntegrationEventPublisher` publishes through Wolverine's **durable outbox**, so the event
is a row in Postgres before the broker sees it, and passes `eventId` and the type name as envelope
**headers**. A custom envelope mapper writes the AMQP properties from those headers, never from the
message object: an envelope recovered from Postgres by another host after an outage reaches the
mapper with no deserialized message, only its headers (verified). Wolverine's serializer adds a
converter to the options it is given, so each endpoint gets a *copy* of the shared, read-only
`IntegrationJson.Options` (verified).

**Inbound flow.** The worker listens on `aiframework.shipments` with a durable inbox and
`DefaultIncomingMessage<ShipmentConfirmedV1>()`, so a producer needs no Wolverine headers — a JSON
body is enough (verified). `ShipmentConfirmedHandler` dispatches a new command, `RecordShipment`,
rather than the operator's `ShipOrder`, which needs a current user the worker does not have. It is
idempotent by order state: a placed order ships; an already-shipped one is a successful no-op; a
cancelled or unknown order, or an invalid message, is rejected straight to dead letters; a
transient failure retries on the job lanes' schedule first. A body that is not JSON dead-letters
at once and the queue keeps moving (verified).

**Jobs on RabbitMQ.** `JobRegistration` routes with `ToRabbitQueue` and listens with
`ListenToRabbitQueue` instead of the Postgres queue transport; parallelism, the error policy,
`JobUserMiddleware`, `IJobScheduler`, Quartz and every job are unchanged. `WolverineFx.Postgresql`
stays: Postgres still holds the outbox, inbox, schedules and dead letters. RabbitMQ has no delayed
delivery and needs none — a scheduled job waits in `wolverine_incoming_envelopes` (as
`message_type = 'scheduled-envelope'`) until due, and a retry waits there too (verified). Dead
letters stay in `wolverine_dead_letters`, RabbitMQ-native dead-lettering is disabled, and the
monitoring page's Retry re-delivers a message that failed on a RabbitMQ listener: the worker's
durability agent picks the replayed envelope up within about 20 seconds (verified, and pinned by
`ShipmentInboundTests.ADeadLetteredShipment_IsRedeliveredByRetry`).

**Configuration and startup.** One setting, `ConnectionStrings:RabbitMq`, an AMQP URI — set as
`ConnectionStrings__RabbitMq`. RabbitMQ is configured inside the durable branch of
`AddWolverineEventPath`, so `Wolverine__Durable=false` turns it off with the rest of the transport
and `codegen write`, the OpenAPI contract and `HealthTests` need no new switch. A durable host with
no connection string refuses to start, naming the key. A host that cannot reach the broker refuses
to start too, after Wolverine's `BrokerInitializationTimeout` (two minutes; verified). **Readiness
does not include the broker:** probing it would pull every API pod out of the load balancer on a
broker blip, which is the opposite of what the durable outbox buys.

**Environments.** RabbitMQ is on by default everywhere, with no opt-in switch, so CI exercises it.
`rabbitmq:4.3-management-alpine` in `docker-compose.yml`'s default profile (AMQP 55672, UI 55673,
named volume, fixed hostname so the volume's data survives a recreate) and, throwaway, in
`docker-compose.e2e.yml` (55682/55683); a one-replica StatefulSet with a PVC and a disruption
budget in the kind cluster, deployed and awaited before the API and worker; and one Testcontainer
per test collection. The dev credential is a committed throwaway, the same documented exception as
the dev Postgres string.

## Consequences

**What this makes easy.** Another system can consume every event this application raises by
binding a queue, with no change here. An inbound integration is a contract, a handler and a
listener. Job delivery is push rather than poll, and queue load leaves the primary database.
Worker replicas are competing consumers on quorum queues, so scaling them is a replica count.

**What this costs, concretely.**

- **A broker in every environment.** A second container in compose, the e2e stack and every
  integration test collection; a StatefulSet, PVC, disruption budget and credentials in the kind
  cluster; one more thing for `dev.ps1` to start and wait on.
- **The API and the worker refuse to start without it.** Deliberately loud, but a bare
  `dotnet run` or IDE F5 now needs `docker compose up -d` first, and a misconfigured broker takes
  about two minutes (the initialization timeout) to fail rather than failing at once.
- **At least once, never exactly once.** A crash between the two stores, or a redelivered domain
  event, publishes twice. Consumers deduplicate on `eventId`, which is only safe because every
  publisher reuses the outbox's `MessageId` rather than minting an id.
- **No ordering between different events.** Independent handlers publish `order.placed` and
  `order.shipped`; a consumer orders by `occurredAt`, not by arrival.
- **Unrouted events are capped, not kept.** With no consumer bound, every event lands in
  `aiframework.events.unrouted`, and past 100,000 the oldest are dropped. That is a debugging aid,
  not an archive.
- **The broker credential is the access control for inbound.** Anyone who can publish to
  `aiframework.shipments` can ship any order. The dev and kind credential can do everything; **a
  real deployment needs a producer-only RabbitMQ user for the warehouse**, allowed to write to that
  queue and nothing else.
- **An outage grows `wolverine_outgoing_envelopes`**, bounded by the outage's length and drained
  automatically. Nothing surfaces the count yet; that is a follow-up.
- **Jobs waiting in the old Postgres queues at upgrade time are not migrated.** The
  `wolverine_queues` tables (`wolverine_queue_jobs_light`, `_heavy` and their `_scheduled`
  companions) are simply no longer read. Drain them with the old worker before the first deploy of
  this change; the `messaging` skill has the check.

**Known limitations, verified rather than guessed.**

- **A partitioned broker hangs startup instead of failing it.** Against a broker that accepts the
  TCP connection and then never answers — a paused container, a network black hole — Wolverine's
  startup waited for over ten minutes, ignoring both the initialization timeout and the host's
  cancellation token. The host never becomes ready, so readiness probes and `dev.ps1`'s
  `docker compose up --wait` ordering contain it, but nothing fails loudly.
- **A partition at runtime blocks the publishing call; an outage does not.** When the broker stops
  (`rabbitmqctl stop_app`), a publish returns at once, the envelope waits in Postgres, and it is
  delivered about five seconds after the broker returns; listeners reconnect on their own. When
  packets are silently dropped instead, the publishing call waits on the dead connection until it
  recovers or times out (about 60 s), holding that outbox worker. The event is not lost either way.

**What this rules out.** Queuing synchronous requests. A second queue technology in the middle of
the system: the Postgres queue transport is gone. It does not rule out replacing the hand-built
domain-event outbox (ADR 0005), which stays exactly as it was and feeds the new publishers.

## Alternatives considered

**Queue every write.** The literal reading of "UI → API → queue → DB". Rejected: every endpoint
becomes `202 Accepted`, read-your-own-writes breaks, every screen needs a pending state, and the
auth path cannot be queued anyway.

**Events only on RabbitMQ; jobs stay on Postgres queues.** The smallest change that meets trigger
4. Rejected because it leaves two queue technologies in the middle of the system, each with its own
failure modes, for no gain.

**Send inline to the broker from the domain-event handler.** No outbox hop, fewer rows. Rejected:
a broker outage throws inside the fan-out, re-runs every other handler of that event (the audit,
the notifiers), and eventually dead-letters the event into the hand-built outbox, which has no
replay.

**Replace the hand-built outbox with Wolverine's.** Would remove the double hop. Rejected as its
own project: the hand-built outbox is ADR 0005's, the notification feed and the audit depend on
it, and nothing here needs it gone.

**Wolverine's native envelope on the wire.** Zero mapping code. Rejected because it assumes the
consumer runs Wolverine: .NET type names in `type`, Wolverine's own headers. A plain JSON body with
standard AMQP properties is readable by anything.

**CloudEvents.** A standard envelope. Rejected as ceremony without a consumer that asks for it; the
AMQP properties already carry id, type and correlation.

**A catalogue feed as the first inbound message, or plumbing only.** A catalogue feed would need a
system identity holding `Catalogue.Manage` (ADR 0025); plumbing with no real message gives nothing
real to test. `shipment.confirmed.v1` exercises the whole round trip, back out as
`order.shipped.v1`.

**Let RabbitMQ drop unroutable events.** The default. Rejected: with no consumer yet, that is every
event, and nothing would show it happening.

**RabbitMQ-native dead-letter queues.** Rejected: invisible to the monitoring page and its Retry
(ADR 0021), which read Wolverine's Postgres storage.

**An opt-in switch for the broker.** Would keep the old no-broker setup available. Rejected because
CI would then never exercise the broker, and the configuration that ships would be the untested
one.

**Choose the transport by probing the broker at startup.** Rejected (as the abandoned 2026-09-06
branch had already concluded): a host that silently degrades when the broker is down fails later
and quietly. Configuration decides; an unreachable broker stops the host.

**Classic queues.** Rejected: RabbitMQ 4 recommends quorum queues for durable data, and mirrored
classic queues are gone.
