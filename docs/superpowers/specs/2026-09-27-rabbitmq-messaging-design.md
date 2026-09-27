# RabbitMQ as the application's message broker

**Date:** 2026-09-27
**Status:** Approved in conversation, section by section; awaiting review of this document
**Supersedes, once implemented:** ADR 0016's "The transport stays PostgreSQL", and the deferral of
RabbitMQ it records. The ADR itself is written in the implementation (ADR 0026).
**Replaces:** the abandoned `feat/rabbitmq-transport` branch (2026-09-06), which predates the
worker host, the notification feed and 270+ commits of `main`. Its reasoning on configuration over
probing and on failing fast carries over; its code does not.

## Intent

In the owner's words: the system flow should be *UI → API → queue → DB, and back again*, with
RabbitMQ as the queue and Wolverine included. There is no RabbitMQ server yet, so it runs as a
Docker container for local work.

What that became, deliberately:

- **Requests stay synchronous.** UI → API → DB stays the path for anything a user waits on. Putting
  a queue in front of every write would make every endpoint answer `202 Accepted`, break
  read-your-own-writes, force a pending state onto every screen, and still could not cover sign-in
  or the per-request security-stamp read (ADRs 0011, 0020). Rejected for this scale.
- **Everything asynchronous goes through RabbitMQ.** Background jobs (API → RabbitMQ → worker → DB),
  events for other systems (DB → outbox → RabbitMQ → out), and messages from other systems
  (in → RabbitMQ → worker → DB). "Back again" to the UI is the existing notification feed and
  SignalR push.
- **Wolverine is the messaging layer in both hosts.** Application code never touches RabbitMQ.

### Success looks like

1. Placing an order publishes `order.placed.v1` to RabbitMQ, where it is visible in the management
   UI, and survives a broker outage by waiting in Postgres.
2. A `shipment.confirmed.v1` message dropped on a queue ships the order, notifies the buyer, and
   publishes `order.shipped.v1`: a round trip.
3. Every job runs API → RabbitMQ → worker, and a failed job still appears on the monitoring page's
   dead letters with a working Retry.
4. `local-run/control-panel.bat` option 2 brings the whole thing up with nothing started by hand,
   and the kind cluster runs it too.

## Decisions

| Decision | Choice | Rejected, and why |
|---|---|---|
| What goes through RabbitMQ | All async work: jobs, outbound events, inbound messages | Every write (see Intent); events only (leaves two queue technologies in the middle of the system) |
| Outbound delivery | Domain-event handler → Wolverine **durable** outbox → RabbitMQ | Inline send (a broker outage re-runs every other handler of the event and dead-letters it into the hand-built outbox, which has no replay); replacing the hand-built outbox (its own project, ADR 0005) |
| Wire format | Plain JSON body of a versioned contract, standard AMQP properties | Wolverine-native envelope (assumes a Wolverine consumer); CloudEvents (ceremony without a consumer that asks for it) |
| First inbound message | `shipment.confirmed.v1` from a warehouse, shipping the order | Catalogue feed (needs a system identity for `Catalogue.Manage`); plumbing only (nothing real to test) |
| Unroutable events | Alternate exchange into a capped `aiframework.events.unrouted` queue | Letting RabbitMQ drop them: with no consumer yet, that is every event |
| Dead letters | Wolverine's Postgres storage, RabbitMQ-native DLQ disabled | Native DLQ (invisible to the monitoring page and its Retry) |
| Where it runs | Everywhere, on by default: dev compose, e2e stack, kind, integration tests | Opt-in switch (CI would never exercise it) |
| Transport selection | Configured when Wolverine is durable; no new switch | A broker probe (degrades silently when the broker is down) |
| Queue type | Quorum, everywhere | Classic (RabbitMQ 4 recommends quorum for durable data; mirrored classic is gone) |

## Architecture

```
 API process                                        RabbitMQ                          Worker process
┌─────────────────────────────┐        ┌───────────────────────────────┐    ┌──────────────────────────────┐
│ IJobScheduler.EnqueueAsync  │───────►│ aiframework.jobs.light   (Q)  │───►│ job handlers (unchanged)     │
│                             │───────►│ aiframework.jobs.heavy   (Q)  │───►│                              │
│ outbox pump (also worker):  │        │                               │    │                              │
│  integration publishers ────┼───────►│ aiframework.events (topic X)  │─┐  │                              │
└─────────────────────────────┘        │   └ alt-exchange ─► .unrouted │ └─►│ (other systems bind here)    │
                                       │ aiframework.shipments    (Q)  │───►│ ShipmentConfirmed → DB       │
             Postgres: Wolverine outbox, inbox, schedules, dead letters (unchanged role)
```

Both hosts publish: the hand-built outbox pump runs in both (`AddOutbox` is in
`AddInfrastructure`), and the API enqueues jobs. **Only the worker listens**; the API still listens
to no queue (ADR 0016), which `ApiPublishesOnlyTests` keeps asserting.

## 1. Broker topology

All objects are durable and declared by Wolverine at startup (`AutoProvision()`).

| Name | Type | Declared by us | Purpose |
|---|---|---|---|
| `aiframework.jobs.light` | quorum queue | yes | The light job lane (today `jobs_light` on Postgres) |
| `aiframework.jobs.heavy` | quorum queue | yes | The heavy job lane (today `jobs_heavy`) |
| `aiframework.events` | topic exchange, `alternate-exchange=aiframework.events.unrouted` | yes | Everything we publish. Routing key = the event name already registered with `AddDomainEvent`: `order.placed`, `order.shipped`, `order.cancelled`, `product.price_changed` |
| `aiframework.events.unrouted` | fanout exchange + quorum queue of the same name, `x-max-length=100000`, `x-overflow=drop-head` | yes | Catches every event no consumer has bound for, so nothing is silently dropped. Capped so it cannot fill a disk when nobody reads it |
| `aiframework.shipments` | quorum queue | yes | Inbound shipment confirmations. Producers publish to it by name through the default exchange |

Consumers of our events declare and bind **their own** queues (`order.*`, `#`, …). Adding a
consumer never touches this application. Once one binds a key, those events stop reaching
`unrouted`.

## 2. Contracts

Plain JSON, System.Text.Json, camelCase properties, enums as names (matching the HTTP API). The
records live in `src/Application/IntegrationEvents`, separate from domain events, so a domain
event can change shape without breaking anyone outside.

**AMQP properties on everything we publish:** `message_id` = `eventId`, `type` = the versioned name,
`content_type` = `application/json`, `correlation_id` = the originating trace id.

| Type (`type`) | Routing key | Body |
|---|---|---|
| `order.placed.v1` | `order.placed` | `eventId, occurredAt, orderId, buyerId, sku, quantity, productName?, unitPrice?` |
| `order.shipped.v1` | `order.shipped` | `eventId, occurredAt, orderId, buyerId, sku` |
| `order.cancelled.v1` | `order.cancelled` | `eventId, occurredAt, orderId, buyerId, sku, reason` |
| `product.price_changed.v1` | `product.price_changed` | `eventId, occurredAt, productId, sku, name, oldPrice, newPrice` |
| `shipment.confirmed.v1` (inbound) | — | `shipmentId, orderId, shippedAt` |

- `eventId` is the hand-built outbox's `MessageId`: stable across redelivery, so it is the
  consumers' dedupe key.
- `OrderPlaced` carries only `OrderId`, `Sku` and `Quantity`, so its publisher reads the order once
  for the buyer, product name and price. Those are snapshots on the order that never change, so a
  late read is safe. `productName` and `unitPrice` are null for orders that predate the catalogue.
- `ProductPriceChanged` already carries all of the last row (`ProductId, Sku, Name, OldPrice, NewPrice`), so it needs no read.
- **Versioning:** fields may be added within v1. Renaming or removing one means a `.v2` type
  published alongside v1 until consumers have moved.
- Inbound needs a JSON body only: `DefaultIncomingMessage<ShipmentConfirmedV1>()` treats anything on
  `aiframework.shipments` as that type, with no Wolverine headers required.

## 3. Outbound flow

```
command commits ─► hand-built outbox row (unchanged)
   └► pump (API or worker) ─► fan-out: audit │ notifier │ job │ NEW integration publisher
                                                              └► IIntegrationEventPublisher (Application port)
                                                                   └► Wolverine publish → durable outbox (PG)
                                                                        └► RabbitMQ aiframework.events, publisher confirms
```

- **Application:** four `IDomainEventHandler<T>` implementations (`OrderPlacedIntegrationPublisher`
  and one per other event) map the domain event to its contract and call
  `IIntegrationEventPublisher.PublishAsync(contract, context.MessageId, ct)`.
- **Infrastructure:** `WolverineIntegrationEventPublisher` calls `IMessageBus.PublishAsync` and
  passes the `eventId` as an envelope header. A custom `IRabbitMqEnvelopeMapper` on the events
  endpoint writes the AMQP properties.
- **Routing:** one rule per contract type to `aiframework.events` with its routing key, and
  `UseDurableOutbox()` on that endpoint.

**Guarantees, stated plainly:**

- **At least once.** A crash between the two stores, or a redelivered domain event, can publish
  twice. Consumers deduplicate on `eventId`.
- **Broker outage.** Envelopes wait in Postgres and drain on recovery. The audit, notifier and job
  handlers never notice, because writing an envelope row is local.
- **No ordering between different events.** Independent handlers publish `order.placed` and
  `order.shipped`. Consumers use `occurredAt`.

## 4. Inbound flow

The worker listens on `aiframework.shipments` with a durable inbox. It uses
`DefaultIncomingMessage<ShipmentConfirmedV1>()`, and native dead-lettering is disabled.
`ShipmentConfirmedHandler` (Infrastructure, a Wolverine adapter like the job adapters) dispatches the
Application command `RecordShipment(OrderId, ShipmentId, ShippedAt)`.

Why a new command and not `ShipOrder`: `ShipOrder` is the operator's command, needs a current user
for its cache eviction, and answers 401 without one; the worker has none. `RecordShipment` uses
`GetForFulfilmentAsync`, the warehouse's `shippedAt`, and no cache tags. The worker's cache is off,
and the buyer's 30-second staleness is the limit ADR 0024 accepted.

**Idempotent by order state:**

| Order | Outcome |
|---|---|
| Placed | Shipped. `OrderShipped` fires: the buyer is notified and `order.shipped.v1` goes out |
| Already shipped | Success, no-op. Covers redelivery and duplicate confirmations |
| Cancelled | Rejected (conflict) |
| Not found | Rejected |
| Empty ids, or `shippedAt` more than 5 minutes in the future | Rejected by the validator |

**Failures:**

- Rejections go straight to dead letters; retrying cannot change the answer.
- Transient failures (the database unreachable) retry with backoff, then dead-letter. This is the
  job lanes' policy shape.
- Both land in `wolverine_dead_letters`, which the monitoring page lists and retries.
- A different `shipmentId` for an already-shipped order is a no-op. Partial shipments are out of
  scope.

**Security:** anyone who can publish to `aiframework.shipments` can ship any order. The broker
credentials are the access control. A real deployment needs a producer-only RabbitMQ user for the
warehouse, and ADR 0026 says so.

## 5. Jobs on RabbitMQ

A configuration change inside `JobRegistration`:

- `ToPostgresqlQueue(QueueFor(lane))` becomes `ToRabbitQueue(QueueFor(lane))`, and
  `ListenToPostgresqlQueue` becomes `ListenToRabbitQueue`.
- `QueueFor` returns `aiframework.jobs.light` and `aiframework.jobs.heavy`.
- Per-lane `MaximumParallelMessages`, the error policy (`ScheduleRetry` then `MoveToErrorQueue`),
  `JobUserMiddleware`, `IJobScheduler`, Quartz and every job are **unchanged**.
- Sends from the API go through a durable outbox; the worker's listeners have a durable inbox.
- Scheduled and retried jobs wait in Wolverine's Postgres storage until due. RabbitMQ has no
  delayed delivery, and doesn't need one.
- **`WolverineFx.Postgresql` stays.** It provides the envelope storage behind the outbox, inbox,
  schedules and dead letters. Only the job *queues* move.
- `RetryDeadLetter` warns that it is "the thing to re-examine if the transport ever becomes
  RabbitMQ": verification item V3 below proves Retry still re-delivers before anything depends on
  it.

## 6. Configuration and startup contract

**One setting:** `ConnectionStrings:RabbitMq`, an AMQP URI.

- Environment variable `ConnectionStrings__RabbitMq`, with double underscores.
- Dev value committed in `appsettings.Development.json` as a throwaway localhost credential, the
  same documented exception as the dev Postgres string.
- Kind: the overlay's `secret.yaml`.
- Anywhere real: user-secrets or the environment.

RabbitMQ is configured **inside the durable branch** of `AddWolverineEventPath`, so
`Wolverine__Durable=false` turns it off with the Postgres transport. `codegen write`, the OpenAPI
contract generation, `HealthTests` and CI's `codegen` and `contract` jobs therefore need no new
switch. Handler **discovery** of `ShipmentConfirmedHandler` stays outside the durable branch, like
the job handlers, so the worker's generated tree includes it.

| Situation | Behaviour |
|---|---|
| Durable, connection string missing | Refuse to start, naming the key (like the `ConnectionStrings:Default` guard) |
| Broker unreachable at startup | Refuse to start: deterministic, and misconfiguration is loud |
| Broker down while running | Keep serving. Outbound envelopes wait in Postgres, and listeners reconnect |

**Readiness does not include the broker.** Probing it would pull every API pod out of the load
balancer on a broker blip, which is the opposite of what the durable outbox buys.

## 7. Environments

### Dev compose (`docker-compose.yml`)

A `rabbitmq` service in the default profile, so a plain `docker compose up` starts it:

- **Image** `rabbitmq:4.3-management-alpine`.
- **`hostname: rabbitmq`.** RabbitMQ keys its data directory on the node name, so a random
  container hostname orphans the volume's messages on every recreate.
- **`RABBITMQ_DEFAULT_USER` / `RABBITMQ_DEFAULT_PASS` = `aiframework`.** Required: `guest` may only
  connect from inside the container, and a host connection through the port mapping does not count.
- **Ports** `${RABBITMQ_PORT:-55672}:5672` (AMQP) and `${RABBITMQ_UI_PORT:-55673}:15672` (management
  UI).
- **Healthcheck** `rabbitmq-diagnostics -q check_port_connectivity`, meaning AMQP is accepting, not
  merely that the node is up.
- **Named volume** `rabbitmqdata`, like `pgdata`.

### Local-run scripts (`local-run/control-panel.bat`)

| Script (menu) | Change |
|---|---|
| `scripts/dev.ps1` (2, 3) | `docker compose up -d --wait` now waits on RabbitMQ too, **before** the API and worker start (both refuse to start without it). Sets `ConnectionStrings__RabbitMq` from `RABBITMQ_PORT`, like the Postgres string from `DEV_PG_PORT`. The port pre-check covers 55672/55673. The Ready summary prints the UI URL and login. |
| `scripts/worker.ps1` (4) | Checks the AMQP port first and says "start the dev loop first" rather than letting the worker die on a connection error |
| `scripts/stop-dev.ps1` (5) | No change; `docker compose down` already stops default-profile services, and messages stay in the volume |
| `scripts/e2e.ps1` (8) | `docker-compose.e2e.yml` gains a throwaway RabbitMQ (no volume, port `${E2E_RABBITMQ_PORT:-55682}`). `playwright.config.ts` passes the connection string to both web servers. CI's e2e job gets it through the same compose file. |
| `local-run/control-panel.bat` | Menu text only: option 2 reads "Postgres + RabbitMQ + API + job worker + Vite" |
| `scripts/install-prereqs.ps1` (1) | No change; the broker is a container |

### Kubernetes (kind)

| Concern | Answer |
|---|---|
| Deploy ordering | `k8s/base/rabbitmq.yaml` (Service and StatefulSet) lands in `deploy.ps1`'s phase A. A new `rollout status statefulset/rabbitmq` wait sits beside the Postgres one, addressed by name, not label, for the reason that script records |
| Identity across restarts | The StatefulSet gives `rabbitmq-0` a stable hostname; data on a PVC |
| Node drains | The PVC pins it to one node, like Postgres. PDB `maxUnavailable: 1` (a one-replica `minAvailable: 1` blocks drains forever) |
| Memory | Requests 256Mi, limit 512Mi. RabbitMQ derives its memory watermark from the cgroup limit |
| Replicas | API: two replicas publishing only. Worker: any count, as competing consumers on quorum queues |
| Credentials | `secret.yaml`: `RABBITMQ_DEFAULT_USER/PASS` and `ConnectionStrings__RabbitMq=amqp://…@rabbitmq:5672/` |
| Management UI | Not on the ingress. `kubectl port-forward svc/rabbitmq 15672` is documented in the kubernetes skill |
| e2e against the cluster | The shipment e2e test publishes to AMQP from the host, which the cluster doesn't expose, so it is `@local-only`. Everything else runs, and exercises outbound publishing and the job lanes implicitly |

## 8. Observability

- Wolverine propagates W3C trace context on RabbitMQ messages, so a job's or shipment's spans join
  the originating trace; the implementation confirms this.
- `Behaviors.LoggedAsync` already logs `RecordShipment`, like every command; no hand-written
  logging.
- A pending-outbound-envelope count on the monitoring page is a follow-up, not part of this.

## 9. Testing strategy

Every layer's rules from tests/CLAUDE.md apply. The integration test projects gain a RabbitMQ
Testcontainer (`Testcontainers.RabbitMq` 4.15.0, matching `Testcontainers.PostgreSql`), **one per
collection**, beside the Postgres one.

| Layer | What is proven |
|---|---|
| Application | Each integration publisher maps its domain event to the right contract with `eventId` = `MessageId`. `RecordShipment`: ships a placed order; no-op on shipped; conflict on cancelled; not-found; validator rules |
| Infrastructure | Contract JSON shape (camelCase, enums as names, no Wolverine-only fields), pinned by a snapshot test. The envelope mapper sets `message_id`, `type`, `content_type` and `correlation_id` |
| Api.IntegrationTests (Postgres + RabbitMQ containers) | Placing an order puts `order.placed.v1` on a test queue bound to `order.#`, with the right routing key and properties. An event nobody binds lands in `aiframework.events.unrouted`. **Outage:** broker stopped, order placed, broker started → the event arrives. The API declares no listener (`ApiPublishesOnlyTests`, re-pointed) |
| Worker.IntegrationTests | Each lane delivers a job through RabbitMQ (`JobDeliveryTests`, re-pointed). A failing job reaches `wolverine_dead_letters` and **Retry re-delivers it** (V3). A scheduled job runs when due. A shipment ships the order and emits `order.shipped.v1`: the round trip. Each rejection dead-letters without retrying. A duplicate shipment is a no-op |
| Codegen | `WolverineCodegenTests` and `WorkerCodegenTests` stay green after regeneration. Release starts: the CI `backend (Release)` job |
| e2e | Existing specs pass unchanged on the managed stack and kind. A new `@local-only` spec publishes a shipment to the broker, and the buyer sees "Order shipped" |
| Local-run | Manual checklist in the plan: control-panel option 2 from a clean machine state; the UI shows the exchange, queues and an event in `unrouted`; option 5 stops it; option 6 on kind |

## 10. Verify before building (task 1)

The docs describe these; the installed 6.40.0 package decides. Task 1 is a throwaway probe against a
real container, and each answer is recorded in the plan before later tasks rely on it.

- **V1.** The routing-key API for a topic exchange (`ToRabbitRoutingKey` vs a topic endpoint URI),
  and declaring an exchange with an `alternate-exchange` argument and a quorum queue with
  `x-max-length` / `x-overflow`.
- **V2.** How an envelope mapper sets `message_id` from our header, and how to make the body plain
  camelCase JSON on one endpoint only.
- **V3.** A dead-lettered message from a RabbitMQ listener lands in `wolverine_dead_letters`, and
  `IDeadLetterStore.ReplayAsync` re-delivers it.
- **V4.** Behaviour when the broker is down at startup (fail fast with `AutoProvision`?) and when it
  drops at runtime (reconnect? outbox drains?).
- **V5.** `DefaultIncomingMessage<T>()` accepts a body with no Wolverine headers and no
  `message_id`.
- **V6.** Scheduled envelopes (`ScheduleAsync`, `ScheduleRetry`) targeting a RabbitMQ endpoint wait
  in Postgres and send when due.

If any answer contradicts this spec, the spec is corrected first.

## 11. Documentation

- **ADR 0026:** RabbitMQ as the broker for asynchronous work. It supersedes ADR 0016's transport
  decision, records trigger 4 ("a consumer outside this solution"), and names the producer-only
  credential requirement.
- **A new `messaging` skill:** topology, contracts, versioning, the durable-outbox guarantees, and
  where dead letters go.
- **Updates:**
  - root CLAUDE.md: the Jobs rules, and the broker in "Running locally"
  - the jobs, local-dev, kubernetes and regenerate skills
  - tests/CLAUDE.md: the scheduled-job note and the Testcontainers section
  - frontend/e2e/CLAUDE.md: the new spec and its tag
  - `docs/local-development.md`
- Delete the stale local `feat/rabbitmq-transport` and `docs/rabbitmq-transport-spec` branches once
  this merges.

## Out of scope

- Queuing synchronous requests (see Intent).
- Replacing the hand-built domain-event outbox with Wolverine's (ADR 0005).
- A RabbitMQ cluster; a single node everywhere.
- Partial shipments; any inbound message other than `shipment.confirmed.v1`.
- A dedicated server. When one exists, it is a connection string.
- Monitoring-page additions for broker state.

## Risks

| Risk | Mitigation |
|---|---|
| Dead-letter Retry or scheduling behaves differently on RabbitMQ | V3 and V6 first; the spec changes before code does |
| Every integration test run now starts a second container | One per collection; the RabbitMQ container starts in about 5s |
| Broker outage grows the outgoing envelope table | Bounded by the outage's length; the drain is automatic; a follow-up surfaces the count |
| Nobody reads `unrouted` | Capped at 100,000 with drop-head; documented as the place to look locally |
| The old branch is mistaken for current work | Deleted after merge, and this spec says it is replaced |

## References

- Wolverine RabbitMQ transport: https://wolverinefx.net/guide/messaging/transports/rabbitmq/ (publishing, interoperability, dead-letter queue pages)
- Wolverine durability: https://wolverinefx.net/guide/durability/
- `WolverineFx.RabbitMQ` 6.40.0 on NuGet, matching the repo's Wolverine pins
- `rabbitmq:4.3-management-alpine` (4.3.6 current at 2026-09-27)
- ADRs 0005, 0007, 0009, 0010, 0014, 0016, 0017, 0019, 0020, 0021, 0024
