---
name: messaging
description: Use when touching RabbitMQ, integration events, the shipments listener, or anything that publishes or consumes a broker message - the topology, versioned contracts, at-least-once delivery and eventId dedupe, outage behaviour, inbound rejection vs retry, where dead letters go, adding an outbound event, why the shipments listener is serial, and upgrading (draining the old Postgres job queues; a PRECONDITION_FAILED on aiframework.shipments).
---

# Messaging

**Requests stay synchronous; everything asynchronous rides RabbitMQ.** Jobs (API → RabbitMQ →
worker), events for other systems (outbox → RabbitMQ → out) and messages from other systems (in →
RabbitMQ → worker) all go through the broker; anything a user waits on is still UI → API → DB.
Wolverine is the messaging layer in both hosts, and **Application never references Wolverine or
RabbitMQ** — it sees `IIntegrationEventPublisher` and `IJobScheduler`. Both hosts publish; **only
the worker listens** (`ApiPublishesOnlyTests`, `TopologyTests`). ADR 0026.

Everything below was verified against a real broker and the installed packages before it was
built; the plan's "Verified API" table (`docs/superpowers/plans/2026-09-27-rabbitmq-messaging.md`,
Task 1) has the evidence. Where the Wolverine docs and that table disagree, the table wins.

## Topology

Declared by Wolverine at startup (`AutoProvision()`); every name is a constant in
`src/Infrastructure/EventPath/RabbitMqTopology.cs`.

| Name | Type | Purpose |
|---|---|---|
| `aiframework.jobs.light` / `.heavy` | quorum queue | The job lanes (the `jobs` skill). Declared by their routes and listeners |
| `aiframework.events` | topic exchange, `alternate-exchange=aiframework.events.unrouted` | Everything we publish. Routing key = the domain event's `AddDomainEvent` name: `order.placed`, `order.shipped`, `order.cancelled`, `product.price_changed` |
| `aiframework.events.unrouted` | fanout exchange + quorum queue, `x-max-length=100000`, `x-overflow=drop-head` | Every event no consumer has bound for. Without it RabbitMQ silently drops them — with no consumer yet, that is every event. Capped, so the oldest go first |
| `aiframework.shipments` | quorum queue, `x-single-active-consumer=true` | Inbound `shipment.confirmed.v1`. Producers publish to it by name, through the default exchange. Consumed one at a time across all replicas (below). Declared by its listener |

- **Consumers declare and bind their own queues** (`order.*`, `#`, …). Adding one never touches
  this application; once one binds a key, those events stop reaching `unrouted`.
- **Quorum everywhere** (`UseQuorumQueues()`), and **no reply queue**: both hosts call
  `DisableSystemRequestReplyQueueDeclaration()`. Without it Wolverine declares a *classic*
  `wolverine.response.<guid>` queue on every host — even a `UseSenderConnectionOnly()` one — and
  **starts a listener on it**, so the API would consume from the broker after all.
  `ServiceCapabilities` does not list that listener; `IWolverineRuntime.Endpoints.ActiveListeners()`
  does, which is why `TopologyTests` asserts on the latter.
- Locally the management UI is `http://localhost:55673` (`aiframework`/`aiframework`); on kind,
  `kubectl port-forward svc/rabbitmq 15672`. `unrouted` is the place to look for "did it publish?".

## Contracts and versioning

Records under `src/Application/IntegrationEvents`, deliberately separate from domain events: a
domain event may change shape freely, an integration event is someone else's dependency.

| `type` | Routing key | Body |
|---|---|---|
| `order.placed.v1` | `order.placed` | `eventId, occurredAt, orderId, buyerId, sku, quantity, productName?, unitPrice?` |
| `order.shipped.v1` | `order.shipped` | `eventId, occurredAt, orderId, buyerId, sku` |
| `order.cancelled.v1` | `order.cancelled` | `eventId, occurredAt, orderId, buyerId, sku, reason` |
| `product.price_changed.v1` | `product.price_changed` | `eventId, occurredAt, productId, sku, name, oldPrice, newPrice` |
| `shipment.confirmed.v1` (inbound) | — | `shipmentId, orderId, shippedAt` |

- **Wire format:** plain JSON, camelCase, enums as names, from `IntegrationJson.Options`
  (`src/Infrastructure/Integration/IntegrationJson.cs`). No Wolverine headers. AMQP properties:
  `message_id` = `eventId`, `type` = the versioned name, `content_type` = `application/json`,
  persistent, `correlation_id` = the originating request's trace id (`OutboundEventTests` pins it).
  `IntegrationJsonTests` pins the body shape — a failure there is a contract change, not a test to
  update.
- **Versioning:** fields may be **added** within `.v1`. Renaming or removing one means a new
  record (`OrderPlacedV2`, `"order.placed.v2"`) published **alongside** v1 until every consumer has
  moved. Never edit a v1 field in place.
- **`eventId` is the hand-built outbox's `MessageId`** (`DomainEventContext.MessageId`), and
  `occurredAt` is its `OccurredAt`. Both are stable across redelivery. Never mint a new id in a
  publisher: that silently breaks every consumer's dedupe.

## What the durable outbox guarantees — and what it does not

```
command commits → hand-built outbox row → pump (API or worker) → fan-out: audit │ notifiers │ jobs │ integration publishers
  integration publisher → IIntegrationEventPublisher → Wolverine durable outbox (Postgres) → aiframework.events (publisher confirms)
```

- **At least once.** A crash between the two stores, or a redelivered domain event, publishes
  twice. Consumers deduplicate on `eventId`.
- **A broker outage delays, never loses.** Publishing writes an envelope row
  (`wolverine.wolverine_outgoing_envelopes`) — local, so the audit, notifier and job handlers in the
  same fan-out never notice. With the broker stopped, the publish returns at once, and the envelope
  is delivered about five seconds after the broker returns; listeners reconnect on their own
  (`BrokerOutageTests`, `AfterABrokerOutage_TheWorkerStillConsumesShipments`).
- **A network partition is different.** When packets are silently dropped (a paused container, a
  black-holed route) the publishing call **blocks** on the dead connection until it recovers or
  times out (~60 s), holding that outbox worker. Still not lost.
- **Recovery by another host works**, but the recovered envelope reaches the mapper with
  `Message == null` — only its headers survive Postgres. That is why
  `WolverineIntegrationEventPublisher` sends `event-id` and `event-type` as headers and
  `IntegrationEnvelopeMapper` reads **only** headers. A mapper that reads `envelope.Message` works
  in every test on the live path and writes no `message_id`/`type` after an outage.
- **No ordering between different events.** Independent handlers publish `order.placed` and
  `order.shipped`; consumers order by `occurredAt`.

**Startup.** A durable host with no `ConnectionStrings:RabbitMq` refuses to start, naming the key.
A broker that refuses the connection fails the host after `BrokerInitializationTimeout` (two
minutes, `BrokerInitializationException`). **A broker that accepts TCP and never answers hangs
startup indefinitely** at "Initializing the Wolverine RabbitMqTransport" — the timeout and the
cancellation token are both ignored. Readiness never checks the broker, by design.

## Inbound: rejection vs retry, and where dead letters go

The worker listens on `aiframework.shipments` with a durable inbox and
`DefaultIncomingMessage<ShipmentConfirmedV1>()`: every message on that queue is that type, and a
producer needs only a JSON body — no Wolverine headers, no `message_id` (a Guid `message_id` is
used as the envelope id; anything else gets a new one). `ShipmentConfirmedHandler`
(Infrastructure) dispatches `RecordShipment` through the normal command pipeline.

| Outcome | Path |
|---|---|
| Placed order | Shipped; `OrderShipped` fires → buyer notified, `order.shipped.v1` published |
| Already shipped (redelivery, duplicate) | Success, no-op |
| Cancelled, not found, or invalid (empty ids, `shippedAt` > 5 min ahead) | `IntegrationMessageRejectedException` → **straight to dead letters**; retrying cannot change the answer |
| Transient (database unreachable) | The worker's global policy: `ScheduleRetry` 1/5/30 min, then dead letters |
| Body is not JSON | `JsonException` → dead letters **at once**, even under the retry policy; the queue behind it keeps moving |

**Serial, across every worker replica — do not "fix" it for throughput.** "Already shipped" is an
in-memory check and `Order` has no concurrency token, so two confirmations for one order handled
at once both ship it: two `OrderShipped` rows, a second buyer notification, and a second
`order.shipped.v1` under a *different* `eventId` that consumers cannot dedupe. Two settings in
`IntegrationEventRegistration.ListenForShipments` prevent it, and both are needed:

- `ConfigureQueue(q => q.Arguments["x-single-active-consumer"] = true)` — the broker delivers to
  one consumer at a time, so replicas take turns instead of splitting the queue; the others stand
  by. Set on the **listener's** queue: AutoProvision applies it there (verified with
  `rabbitmqctl list_queues name arguments`).
- `.Sequential()` — that consumer's process handles one message at a time (the listener is durable,
  the mode this applies to). Without it one replica still runs its prefetched messages in parallel.

`ShipmentInboundTests.DuplicateShipmentsArrivingTogether_ShipTheOrderOnce` found three rows for
three confirmations before this, and `Startup_DeclaresTheShipmentsQueueWithASingleActiveConsumer`
reads the argument back from the broker. A new inbound listener whose handler is idempotent only
by an in-memory state check needs the same two settings.

**Dead letters live in Postgres, never on the broker.** RabbitMQ-native dead-lettering is off
(`DisableDeadLetterQueueing()`); every failure — job or inbound — lands in
`wolverine.wolverine_dead_letters` (`received_at = rabbitmq://queue/…`), which the monitoring page
lists. **Retry works on RabbitMQ**: it marks the row replayable and the worker's durability agent
re-runs it about 20 s later, with the same envelope id
(`ShipmentInboundTests.ADeadLetteredShipment_IsRedeliveredByRetry`; tests allow 60 s).

**Security: the broker credential is the access control.** Anyone who can publish to
`aiframework.shipments` can ship any order. The dev and kind credential is `RABBITMQ_DEFAULT_USER`: the
`administrator` tag and `.*` configure/write/read on vhost `/`. A
real deployment needs a **producer-only RabbitMQ user** for the warehouse, with write permission on
that queue and nothing else.

## Adding a new outbound event

1. **Contract record** in `src/Application/IntegrationEvents/OutboundContracts.cs`:
   `sealed record XxxV1(Guid EventId, DateTimeOffset OccurredAt, …) : IIntegrationEvent`, with
   `TypeName => "xxx.v1"` and `RoutingKey =>` the domain event's `AddDomainEvent` name.
2. **Descriptor** in `IntegrationEventRegistration.Outbound`
   (`src/Infrastructure/Integration/IntegrationEventRegistration.cs`):
   `IntegrationEventDescriptor.For<XxxV1>()`. That is the route, the mapper and the serializer;
   `IntegrationEventRegistrationTests` fails on an omission or a duplicate type name.
3. **Publisher handler** in `IntegrationPublishers.cs`: an `IDomainEventHandler<Xxx>` that maps
   and calls `IIntegrationEventPublisher.PublishAsync`, passing `context.MessageId` and
   `context.OccurredAt`. It writes nothing of its own. If the domain event lacks a field, read it
   (as `OrderPlacedIntegrationPublisher` does) only if it is a snapshot that never changes.
4. **Register** it beside the others in `InfrastructureRegistration.cs`
   (`services.AddScoped<IDomainEventHandler<Xxx>, XxxIntegrationPublisher>()`).
5. **Tests:** the mapping in `tests/Application.Tests/IntegrationEvents`, the JSON shape in
   `IntegrationJsonTests`, delivery with its properties in `OutboundEventTests`.
6. **Regenerate both Wolverine trees** (the `regenerate` skill) and let `git diff` decide. A route
   alone usually changes nothing; an inbound handler always changes the worker's tree.

Two traps, both verified: pass each Wolverine serializer a **copy** —
`new SystemTextJsonSerializer(new JsonSerializerOptions(IntegrationJson.Options))` — because it
adds a converter to the options it is given and throws on the shared read-only instance; and
`ExchangeType` is ambiguous between `Wolverine.RabbitMQ` and `RabbitMQ.Client` when both are
imported.

**A new inbound message** follows `ShipmentConfirmedV1`: a listener beside `ListenForShipments`
(worker only, inside the durable branch), and handler **discovery** outside it
(`opts.Discovery.IncludeType<…>()` in `AddWolverineEventPath`), or `codegen write` — which runs
non-durable — never sees the handler and Release fails at startup.

## Upgrading from Postgres job queues

Before this change, jobs sat in the Postgres queue transport's own schema. **Nothing migrates
them**: after the first deploy of RabbitMQ those tables are simply never read again. Before that
deploy, with the old build still running, confirm they are empty:

```sql
select 'light', count(*) from wolverine_queues.wolverine_queue_jobs_light
union all select 'light scheduled', count(*) from wolverine_queues.wolverine_queue_jobs_light_scheduled
union all select 'heavy', count(*) from wolverine_queues.wolverine_queue_jobs_heavy
union all select 'heavy scheduled', count(*) from wolverine_queues.wolverine_queue_jobs_heavy_scheduled;
```

If any count is not 0, let the old worker drain them first. A scheduled retry can be up to 30
minutes out (the last `ScheduleRetry` step). A fresh database has no `wolverine_queues` tables at
all, and there is nothing to do.

## Upgrading a broker that already has `aiframework.shipments`

The queue gained `x-single-active-consumer=true` late in this branch. RabbitMQ refuses to
redeclare an existing queue with different arguments, so a broker that ran an earlier build — a
dev `rabbitmqdata` volume, a kind PVC — stops the worker at startup with:

```
PRECONDITION_FAILED - inequivalent arg 'x-single-active-consumer' for queue 'aiframework.shipments'
in vhost '/': received the value 'true' of type 'bool' but current is none
```

Stop the worker, delete the queue once, start the worker, which declares it afresh. Any
confirmations still on the queue go with it, so let it empty first (management UI, or
`rabbitmqctl list_queues name messages`):

```powershell
docker compose exec rabbitmq rabbitmqctl delete_queue aiframework.shipments        # dev
kubectl -n aiframework exec rabbitmq-0 -- rabbitmqctl delete_queue aiframework.shipments   # kind
```

**Not `docker compose down -v`**: it deletes every named volume, `pgdata` — the dev database —
included. A fresh broker has no queue and needs nothing. Nothing migrates this in code: the branch
was never released.

## Testing

`tests/CLAUDE.md` has the rules: one RabbitMQ Testcontainer per collection beside Postgres,
`BrokerProbe` for reading and publishing the way an outside system would, and outages simulated
with `rabbitmqctl stop_app`/`start_app` — never `PauseAsync`, which simulates a partition and
blocks the publishing call.
