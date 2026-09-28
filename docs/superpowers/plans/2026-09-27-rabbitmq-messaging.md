# RabbitMQ Messaging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make RabbitMQ the broker for all asynchronous work — background jobs, events published
to other systems, and messages received from them — with Wolverine as the messaging layer in both
hosts, running locally in Docker, in the e2e stack, in the kind cluster and in the integration
tests.

**Architecture:** Wolverine's existing durable (Postgres-backed) outbox, inbox, schedules and dead
letters stay exactly where they are; only the *transport* changes. Job lanes move from Postgres
queues to RabbitMQ quorum queues; four new domain-event handlers publish versioned plain-JSON
integration events to a topic exchange with an alternate exchange for unrouted events; the worker
consumes `shipment.confirmed.v1` from a quorum queue and ships the order through a new
`RecordShipment` command. RabbitMQ is configured inside the durable branch of
`AddWolverineEventPath`, so `Wolverine__Durable=false` keeps every infrastructure-free startup path
broker-free.

**Tech Stack:** .NET 10, WolverineFx 6.40.0 (+ `WolverineFx.RabbitMQ` 6.40.0), RabbitMQ 4.3
(`rabbitmq:4.3-management-alpine`), RabbitMQ.Client 7 (transitive), xUnit + FluentAssertions +
NSubstitute, Testcontainers 4.15.0 (`Testcontainers.RabbitMq`), Playwright, Kustomize/kind.

**Spec:** `docs/superpowers/specs/2026-09-27-rabbitmq-messaging-design.md` — read it first; this
plan argues from it and does not repeat its reasoning.

## Global Constraints

- PR type: **`feat(messaging)!:`** — the API and worker now refuse to start without a broker.
- Package versions: `WolverineFx.RabbitMQ` **6.40.0** (must match every other `WolverineFx.*`
  pin); `Testcontainers.RabbitMq` **4.15.0** (must match `Testcontainers.PostgreSql`).
- Image: `rabbitmq:4.3-management-alpine` everywhere (compose, e2e compose, kind, Testcontainers).
- Names, verbatim: exchanges `aiframework.events` (topic) and `aiframework.events.unrouted`
  (fanout); queues `aiframework.events.unrouted`, `aiframework.shipments`,
  `aiframework.jobs.light`, `aiframework.jobs.heavy` — all **quorum**. Unrouted cap:
  `x-max-length=100000`, `x-overflow=drop-head`.
- Contract type names, verbatim: `order.placed.v1`, `order.shipped.v1`, `order.cancelled.v1`,
  `product.price_changed.v1`, `shipment.confirmed.v1`. Routing keys: `order.placed`,
  `order.shipped`, `order.cancelled`, `product.price_changed`.
- Config key: `ConnectionStrings:RabbitMq` (env `ConnectionStrings__RabbitMq` — **double**
  underscores). Dev value `amqp://aiframework:aiframework@localhost:55672/`.
- Ports: dev AMQP `55672`, dev UI `55673` (`RABBITMQ_PORT`, `RABBITMQ_UI_PORT`); e2e AMQP `55682`,
  e2e UI `55683` (`E2E_RABBITMQ_PORT`, `E2E_RABBITMQ_UI_PORT`).
- Repo rules (root CLAUDE.md): warnings are errors; nullable on; never `catch (Exception)`;
  `throw;` never `throw ex;`; no hand-written "handling X" logging; never log a request instance;
  no secrets in appsettings except the documented throwaway localhost values; dependency rule —
  Application references no Wolverine/RabbitMQ package.
- The API listens to **no** queue. Only the worker (`WolverineHostRole.ProcessesJobs`) gets
  listeners.
- After any handler/route change: regenerate **both** Wolverine trees
  (`dotnet run --project src/Api -- codegen write`, same for `src/Worker`) with
  `ConnectionStrings__Default=<placeholder>` and `Wolverine__Durable=false`. Keep committed files
  whose only Windows diff is line endings or statement reordering in handlers you did not touch.
- Tests: `MethodName_Scenario_ExpectedOutcome`; one container per collection; no `Thread.Sleep`.
  A bounded real-time wait is allowed only for a broker's own delivery and must say why in a comment
  (precedent: `ExchangeRateClientTests`).

## Review Focus

The inputs the spec implies but its main flows do not exercise, most likely to bite first. Each
has a test in the task named.

1. **An external producer sends PascalCase or extra unknown properties** in `shipment.confirmed.v1`
   → it still deserializes (case-insensitive, unknown members ignored). Task 5, Step 1.
2. **A shipment body that is not valid JSON** → the message dead-letters once; the listener does
   not crash-loop or block the queue. Task 8, Step 9.
3. **A shipment whose `shippedAt` is earlier than the order was placed** → rejected, not applied.
   Task 8, Step 1.
4. **An order that predates the catalogue** (its product columns are null in the database — the
   domain cannot construct one, so it exists only as a legacy row) → `productName` and
   `unitPrice` are `null` in `order.placed.v1`, never a crash. Task 6, Step 6.
5. **The broker restarts while the worker is listening** → the worker consumes shipments sent
   after it comes back. Task 7, Step 5.

---

## File Structure

**Create**

| File | Responsibility |
|---|---|
| `src/Application/IntegrationEvents/IIntegrationEvent.cs` | Marker + static `TypeName`/`RoutingKey` contract for outbound events |
| `src/Application/IntegrationEvents/OutboundContracts.cs` | `OrderPlacedV1`, `OrderShippedV1`, `OrderCancelledV1`, `ProductPriceChangedV1` |
| `src/Application/IntegrationEvents/ShipmentConfirmedV1.cs` | The inbound contract |
| `src/Application/IntegrationEvents/IIntegrationEventPublisher.cs` | The port domain-event handlers publish through |
| `src/Application/IntegrationEvents/IntegrationPublishers.cs` | The four `IDomainEventHandler<T>` that map and publish |
| `src/Application/Orders/RecordShipment.cs` | Command, validator, handler, `ShipmentOutcome` |
| `src/Infrastructure/EventPath/RabbitMqTopology.cs` | Every broker name, and the topology declaration |
| `src/Infrastructure/Integration/IntegrationJson.cs` | The one `JsonSerializerOptions` for contracts |
| `src/Infrastructure/Integration/IntegrationEventRegistration.cs` | Outbound descriptor list + routing + inbound listener |
| `src/Infrastructure/Integration/IntegrationEnvelopeMapper.cs` | AMQP properties for outbound contracts |
| `src/Infrastructure/Integration/WolverineIntegrationEventPublisher.cs` | Port adapter over `IMessageBus` |
| `src/Infrastructure/Integration/ShipmentConfirmedHandler.cs` | Wolverine adapter → `RecordShipment`; rejection exception |
| `tests/Application.Tests/IntegrationEvents/IntegrationPublisherTests.cs` | Mapping tests |
| `tests/Application.Tests/Orders/RecordShipmentHandlerTests.cs` | Command tests |
| `tests/Infrastructure.Tests/Integration/IntegrationJsonTests.cs` | Wire-shape tests |
| `tests/Infrastructure.Tests/Integration/IntegrationEventRegistrationTests.cs` | Completeness tests |
| `tests/Api.IntegrationTests/Messaging/BrokerProbe.cs` | Test helper: bind/read/publish via RabbitMQ.Client |
| `tests/Api.IntegrationTests/Messaging/TopologyTests.cs` | Startup declares the topology |
| `tests/Api.IntegrationTests/Messaging/OutboundEventTests.cs` | Placing an order publishes; unrouted is kept |
| `tests/Api.IntegrationTests/Messaging/BrokerOutageTests.cs` | Envelope waits in Postgres, drains on recovery |
| `tests/Worker.IntegrationTests/Messaging/BrokerProbe.cs` | Same helper for the worker project |
| `tests/Worker.IntegrationTests/Messaging/ShipmentInboundTests.cs` | Ships, round trip, rejections, retry, reconnect |
| `k8s/base/rabbitmq.yaml` | Service + StatefulSet + PDB |
| `frontend/e2e/support/broker.ts` | Publish to the e2e broker via the management HTTP API |
| `frontend/e2e/specs/orders/shipment-inbound.spec.ts` | `@local-only` end-to-end shipment |
| `docs/adr/0026-rabbitmq-for-asynchronous-work.md` | The decision |
| `.claude/skills/messaging/SKILL.md` | How to work in this area |

**Modify** — `src/Infrastructure/AiFramework.Infrastructure.csproj`,
`src/Infrastructure/EventPath/WolverineEventPath.cs`, `src/Infrastructure/Jobs/JobRegistration.cs`,
`src/Infrastructure/InfrastructureRegistration.cs`, `src/Application/Abstractions/DomainEventHandling.cs`,
`src/Infrastructure/Outbox/OutboxWorkItemProcessor.cs` (+ its work item),
`src/Application/Orders/IOrderRepository.cs`, `src/Infrastructure/Persistence/OrderRepository.cs`,
`src/Api/Program.cs`, `src/Worker/Program.cs`, both `appsettings.Development.json`,
`docker-compose.yml`, `docker-compose.e2e.yml`, `scripts/dev.ps1`, `scripts/worker.ps1`,
`local-run/control-panel.bat`, `frontend/e2e/support/env.ts`, `frontend/playwright.config.ts`,
`k8s/base/kustomization.yaml`, `k8s/overlays/local/secret.yaml`, `deploy/deploy.ps1`,
`tests/Api.IntegrationTests/ApiFactory.cs`, `tests/Worker.IntegrationTests/WorkerFactory.cs`,
`tests/Worker.IntegrationTests/Jobs/JobDeliveryTests.cs`, the three test `.csproj` files, both
generated trees, docs listed in Task 11.

---

## Task 1: Verification probe (V1–V6) and packages

The spec's section 10 lists six behaviours the docs describe but the 6.40.0 package decides. This
task answers them against a real broker **before** anything relies on them, and records the
answers in this plan. The probe code is throwaway; the package references are kept.

**Files:**
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj`
- Modify: `tests/Api.IntegrationTests/AiFramework.Api.IntegrationTests.csproj`,
  `tests/Worker.IntegrationTests/AiFramework.Worker.IntegrationTests.csproj`,
  `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj`
- Create (then delete in Step 6): `tests/Worker.IntegrationTests/Probe/RabbitMqProbeTests.cs`
- Modify: this plan's "Verified API" section below

**Interfaces:**
- Produces: the "Verified API" table below, which Tasks 3–8 read.

- [ ] **Step 1: Add the packages**

In `src/Infrastructure/AiFramework.Infrastructure.csproj`, beside `WolverineFx.Postgresql`:

```xml
    <PackageReference Include="WolverineFx.RabbitMQ" Version="6.40.0" />
```

In each of the three test projects, beside `Testcontainers.PostgreSql`:

```xml
    <PackageReference Include="Testcontainers.RabbitMq" Version="4.15.0" />
```

Run: `dotnet restore && dotnet build --nologo -v q`
Expected: `Build succeeded.` with 0 warnings.

- [ ] **Step 2: Write the probe**

`tests/Worker.IntegrationTests/Probe/RabbitMqProbeTests.cs` — one test per question, each printing
what it finds via `ITestOutputHelper` rather than asserting a guess:

```csharp
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.RabbitMQ;
using Xunit.Abstractions;

namespace AiFramework.Worker.IntegrationTests.Probe;

// THROWAWAY. Answers V1-V6 in docs/superpowers/plans/2026-09-27-rabbitmq-messaging.md, then is
// deleted in the same task. Not a regression test.
public sealed class RabbitMqProbeTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit =
        new RabbitMqBuilder("rabbitmq:4.3-management-alpine").Build();

    public Task InitializeAsync() => _rabbit.StartAsync();

    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();

    public sealed record ProbeEvent(Guid EventId, string Sku);

    [Fact]
    public async Task V1_TopologyDeclaresWithArguments()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var rabbit = opts.UseRabbitMq(new Uri(_rabbit.GetConnectionString())).AutoProvision();
                rabbit.DeclareExchange("probe.unrouted", ex =>
                {
                    ex.ExchangeType = ExchangeType.Fanout;
                    ex.BindQueue("probe.unrouted");
                });
                rabbit.DeclareQueue("probe.unrouted", q =>
                {
                    q.QueueType = QueueType.quorum;
                    q.Arguments["x-max-length"] = 100_000;
                    q.Arguments["x-overflow"] = "drop-head";
                });
                rabbit.DeclareExchange("probe.events", ex =>
                {
                    ex.ExchangeType = ExchangeType.Topic;
                    ex.Arguments["alternate-exchange"] = "probe.unrouted";
                });
                opts.PublishMessage<ProbeEvent>().ToRabbitRoutingKey("probe.events", "probe.key");
            })
            .StartAsync();

        await host.MessageBus().PublishAsync(new ProbeEvent(Guid.NewGuid(), "SKU"));

        // Unbound routing key -> must land in probe.unrouted via the alternate exchange.
        var factory = new ConnectionFactory { Uri = new Uri(_rabbit.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        BasicGetResult? got = null;
        for (var i = 0; i < 50 && got is null; i++)
        {
            got = await channel.BasicGetAsync("probe.unrouted", autoAck: true);
            if (got is null) await Task.Delay(100);
        }

        output.WriteLine($"V1 unrouted message: {(got is null ? "NONE" : Encoding.UTF8.GetString(got.Body.Span))}");
        output.WriteLine($"V1 properties: type={got?.BasicProperties.Type} id={got?.BasicProperties.MessageId}");
        got.Should().NotBeNull();
    }
}
```

Add one test each for V2–V6 in the same style (each builds its own host against the container):

- **V2:** publish with `.UseInterop(new ProbeMapper())` and
  `.DefaultSerializer(new SystemTextJsonSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web)))`
  on the route, where `ProbeMapper : IRabbitMqEnvelopeMapper` sets `MessageId`, `Type` and
  `ContentType` on the outgoing properties. Print the body and every property and header received.
  Record the exact mapper signature that compiles and whether Wolverine's own headers still appear.
- **V3:** a listener on `probe.in` with `DisableDeadLetterQueueing()`, `UseDurableInbox()`,
  durable Postgres storage (start a `PostgreSqlContainer` in that test), and a handler that throws
  a custom exception mapped to `MoveToErrorQueue()`. Publish once; read `wolverine.wolverine_dead_letters`;
  call the same replay the app uses (`IDeadLetterStore.ReplayAsync` shape: mark `replayable=true`)
  and observe the handler run again. Record the dead-letter table name and whether replay works.
- **V4:** start a host with the broker **paused** (`await _rabbit.PauseAsync()` before
  `StartAsync`) — record whether startup throws, and after how long. Then with a running host:
  pause, publish (durable outbox), read `wolverine.wolverine_outgoing_envelopes`, unpause, and
  record how long until the message arrives. Record whether `PauseAsync`/`UnpauseAsync` exist on
  the Testcontainers 4.15.0 container.
- **V5:** a listener with `.DefaultIncomingMessage<ProbeEvent>()`; publish raw JSON with
  RabbitMQ.Client — no headers, no `message_id` — and record whether the handler receives it and
  what envelope id Wolverine assigns.
- **V6:** `ScheduleAsync` a message routed to a RabbitMQ queue 1 hour out; record which Postgres
  table/row holds it. Then schedule one 2 seconds out and record that it arrives.

- [ ] **Step 3: Run the probe**

Run: `dotnet test tests/Worker.IntegrationTests --filter "FullyQualifiedName~RabbitMqProbe" --logger "console;verbosity=detailed"`
Expected: every test prints its findings. Failures are findings too — record them.

- [ ] **Step 4: Record the answers**

Fill the table below with what actually compiled and happened. Where an answer differs from the
spec, edit the spec first (same commit) and adjust the named later task.

### Verified API (filled in by Task 1)

| # | Question | Answer (exact API / table / behaviour) | Spec or task change |
|---|---|---|---|
| V1 | Routing-key API; exchange/queue arguments; quorum | **Works as the plan wrote it**, with two namespace facts. `opts.PublishMessage<T>().ToRabbitRoutingKey(exchange, key)` (namespace `Wolverine.RabbitMQ`). `rabbit.DeclareExchange(name, ex => { ex.ExchangeType = ...; ex.Arguments["alternate-exchange"] = ...; ex.BindQueue(queue); })` and `rabbit.DeclareQueue(name, q => { q.QueueType = QueueType.quorum; q.Arguments["x-max-length"] = 100_000; q.Arguments["x-overflow"] = "drop-head"; })` all declare exactly (rabbitmqctl: `probe.events topic [{"alternate-exchange","probe.unrouted"}]`; `probe.unrouted quorum [{"x-max-length",100000},{"x-overflow","drop-head"},{"x-queue-type","quorum"}]`), and an unbound routing key lands in the alternate exchange's queue. `.UseQuorumQueues()` makes every queue Wolverine declares for a route/listener quorum (`probe.implicit quorum`). `.AutoProvision().DisableDeadLetterQueueing().UseQuorumQueues().ConfigureChannelCreation(c => { c.PublisherConfirmationsEnabled = true; c.PublisherConfirmationTrackingEnabled = true; })` compiles and runs. **Namespaces:** the transport expression type is `Wolverine.RabbitMQ.Internal.RabbitMqTransportExpression`; `ExchangeType` is ambiguous between `Wolverine.RabbitMQ` and `RabbitMQ.Client` when both are imported — write `Wolverine.RabbitMQ.ExchangeType`. **V1b — a sender-only host still listens:** with `.UseSenderConnectionOnly()` Wolverine still declares a classic, auto-delete `wolverine.response.<guid>` queue and starts a listener on it (`IWolverineRuntime.Endpoints.ActiveListeners()` shows `rabbitmq://queue/wolverine.response.…`); `.DisableSystemRequestReplyQueueDeclaration()` removes both. `ServiceCapabilities.ReadFrom(...).MessagingEndpoints` does **not** list that reply listener, so a capabilities-based "listens to nothing" test is blind to it. RabbitMQ.Client 7: `BasicGetAsync`, `QueueDeclarePassiveAsync` (returns `MessageCount`), `ExchangeDeclarePassiveAsync` (throws `OperationInterruptedException` 404 on a missing exchange and closes the channel), `BasicPublishAsync(exchange, key, mandatory, BasicProperties, body)` all as the plan wrote. | **Task 3:** `RabbitMqTopology` needs `using Wolverine.RabbitMQ.Internal;` and `Wolverine.RabbitMQ.ExchangeType` if `RabbitMQ.Client` is imported. `ConfigureRabbitMq` calls `.DisableSystemRequestReplyQueueDeclaration()` on **both** hosts (nothing in the app uses request/reply, and the reply queue is classic, contrary to "quorum everywhere"). `TheApi_ListensOnNoRabbitMqQueue` asserts on `runtime.Endpoints.ActiveListeners()` (scheme `rabbitmq`), not on `ServiceCapabilities`. Spec §1 and §6 updated. |
| V2 | Mapper signature; per-endpoint serializer; Wolverine headers present? | **Mapper compiles exactly as the plan wrote it:** `Wolverine.RabbitMQ.Internal.IRabbitMqEnvelopeMapper` with `void MapEnvelopeToOutgoing(Envelope envelope, IBasicProperties outgoing)` and `void MapIncomingToEnvelope(Envelope envelope, IReadOnlyBasicProperties incoming)`. On a route with `.UseInterop(mapper).DefaultSerializer(new SystemTextJsonSerializer(options))` the message arrived with `type=probe.event.v1 message_id=<EventId> content_type=application/json delivery_mode=Persistent correlation_id=<envelope.CorrelationId>`, body plain camelCase JSON `{"eventId":…,"sku":"SKU-V2"}`, and **headers null: no Wolverine headers at all**. `envelope.Message` is non-null in the mapper on the live path and after a runtime outage (V4c). Control route without the mapper: `type=<.NET full type name>`, `message_id=<envelope id>`, plus Wolverine headers (`conversation-id`, `reply-uri`, `sent-at`, `source`, `wolverine-protocol-version`, `accepted-content-types`). **V2b — two traps in the plan's `IntegrationJson`:** (1) `options.MakeReadOnly()` on options with no `TypeInfoResolver` throws `InvalidOperationException` ("must specify a TypeInfoResolver setting before being marked as read-only") — the type initializer would fail; `MakeReadOnly(populateMissingResolver: true)` works. (2) `new SystemTextJsonSerializer(options)` **mutates** the options it is given (adds `Wolverine.Runtime.Serialization.CustomJsonConverterForType`) and so throws ("This JsonSerializerOptions instance is read-only…") on read-only options; it works on a copy, `new JsonSerializerOptions(readOnlyOptions)`. | **Task 5 Step 3:** `IntegrationJson.Create()` ends with `options.MakeReadOnly(populateMissingResolver: true)`. **Task 6 Step 5 / Task 8 Step 4:** every Wolverine serializer is `new SystemTextJsonSerializer(new JsonSerializerOptions(IntegrationJson.Options))` — a private copy, because Wolverine writes to it. Spec §2 notes it. |
| V3 | Dead-letter table; replay re-delivers from a RabbitMQ listener? | **Yes — Retry works on RabbitMQ.** With `.DisableDeadLetterQueueing()` on the transport and a policy `OnException<T>().MoveToErrorQueue()`, a failed message from `ListenToRabbitQueue("probe.in")` lands in **`wolverine.wolverine_dead_letters`** (Postgres) within ~1.3 s, with `exception_type`, `exception_message`, `message_type` = the .NET type, `received_at=rabbitmq://queue/probe.in`, `replayable=False`; no native DLQ queue is declared on the broker. `IMessageStore.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery { PageNumber = 1, PageSize = 100 }, ct)` lists it; `ReplayAsync(new DeadLetterEnvelopeQuery([id]), ct)` flips `replayable=True`, and the durability agent re-ran the handler **18.7 s** later with the **same envelope id** (it polls; allow ≥ 60 s in tests). After a successful replay the dead-letter row is gone and the incoming row is `Handled`. | None to the spec. Task 8's retry test must wait ≥ 60 s after `ReplayAsync` (durability-agent polling), not the 30 s `Delivery`. |
| V4 | Broker down at startup; runtime outage drain time; outgoing table name; Pause/Unpause available? | *Partly recorded; V4a/V4d/V4e pending.* **V4b — nothing listening at startup:** `StartAsync` throws `Wolverine.Transports.BrokerInitializationException: Unable to initialize the Broker rabbitmq in time` (inner `BrokerUnreachableException` → `ConnectFailureException`) after **~122 s**: 14 attempts within `WolverineOptions.BrokerInitializationTimeout` (default 2 min). Fail-fast, but slowly. **V4c — runtime outage (container paused), durable outbox:** the envelope sits in **`wolverine.wolverine_outgoing_envelopes`** (`destination=rabbitmq://queue/…`, `owner_id=1`), and `IMessageBus.PublishAsync` **does not return while the broker is paused** — it completed only when the broker came back (23 s pause), and in an earlier 80 s pause it returned after ~60 s. Delivered ≤ 5 ms after unpause, row deleted, with or without publisher confirms. Testcontainers 4.15.0 `PauseAsync()`/`UnpauseAsync()` exist; `GetConnectionString()` throws ("Exposed port 5672/tcp is not mapped") while paused, so read it before pausing. **V4e — real connection loss** (`rabbitmqctl stop_app` inside the container via `RabbitMqContainer.ExecAsync(["rabbitmqctl", "stop_app"])`, which closes every AMQP connection but keeps the container and its mapped port; `start_app` brings it back): `PublishAsync` **returned at once** (complete within 3 s, not faulted), the envelope waited in `wolverine_outgoing_envelopes`, and it was **delivered 4.7 s after `start_app`**; the listener reconnected on its own and handled a message published after the restart (~6 s after `start_app`). So the spec's outage guarantee holds for a broker that is down or restarting; a *paused* container is a TCP black hole that blocks the publishing call instead. | **Task 7:** simulate the outage with `stop_app`/`start_app`, not `PauseAsync`/`UnpauseAsync` — pausing blocks the publishing handler inside `DrainOutboxUntilEmptyAsync` and tests a network partition, not a broker outage. The factory helpers become `StopBrokerAppAsync()`/`StartBrokerAppAsync()` over `ExecAsync`; allow 60 s for delivery after `start_app`. Spec §3 notes the partition case. **V4d — recovery by another host:** host A published during an outage and stopped; its row's `owner_id` went 1 → 0; host B, started after the broker returned, sent it **14.5 s** after starting. **On that recovered path `envelope.Message` is `null` in the envelope mapper** (the body goes out from the stored bytes), so a mapper reading `envelope.Message` writes no `type` and no `message_id`. `envelope.Headers` **does** survive the round trip through Postgres: headers set at publish time with `new DeliveryOptions().WithHeader("event-id", …).WithHeader("event-type", …)` were readable in the mapper on host B, and `envelope.MessageType` held the .NET type name. | **Task 6 Step 5:** `WolverineIntegrationEventPublisher` publishes with `new DeliveryOptions().WithHeader(IntegrationEnvelopeMapper.EventIdHeader, e.EventId.ToString()).WithHeader(IntegrationEnvelopeMapper.EventTypeHeader, TEvent.TypeName)`, and `IntegrationEnvelopeMapper.MapEnvelopeToOutgoing` reads `message_id` and `type` from `envelope.Headers` only — never `envelope.Message` (the spec §3 already says "passes the eventId as an envelope header"; now the type name too). A unit test pins the mapper on an envelope with `Message = null`. |
| V5 | Raw JSON, no headers, no message_id accepted? assigned id? | **Yes.** `ListenToRabbitQueue(q).DefaultIncomingMessage<T>().DefaultSerializer(new SystemTextJsonSerializer(webOptions))` handled raw JSON published with RabbitMQ.Client and no headers, including a PascalCase body with an extra unknown property. Envelope id: `message_id` when it parses as a Guid (used verbatim); otherwise (absent, or `warehouse-123`) Wolverine assigns a new Guid — no failure. **Non-JSON body:** `System.Text.Json.JsonException` → straight to `wolverine_dead_letters` (`source=unknown`), and the next message behind it is handled. **V5b:** same with the worker's global `OnAnyException().ScheduleRetry(1, 5, 30 min).Then.MoveToErrorQueue()` — the poison message still dead-letters immediately (no retry) and the queue keeps moving. | None to the spec. Task 8 Step 9's fallback policy is not needed. |
| V6 | Where a scheduled RabbitMQ-bound message waits; delivered when due? | **Yes.** `IMessageBus.ScheduleAsync(msg, delay)` for a message routed `ToRabbitQueue(...)` writes a row to **`wolverine.wolverine_incoming_envelopes`** with `status='Scheduled'`, `execution_time` = due time, **`message_type='scheduled-envelope'`** (not the .NET type), `received_at=local://durable/`; the body bytes contain the .NET type name (`position('ProbeEvent'::bytea in body) > 0` is true). A 2 s schedule arrived on the queue after ~6.3 s (durability-agent polling); the 1 h one stayed put. **V6b — `ScheduleRetry` on a RabbitMQ listener:** the failed envelope is held in the same table, `status='Scheduled'`, `message_type` = the .NET type, `received_at=rabbitmq://queue/…`, and re-runs when due (5 s retry ran at ~7 s). | **Task 4 Step 1:** the scheduled-job query must match the body, not `message_type`: `where status = 'Scheduled' and message_type = 'scheduled-envelope' and position('SendOrderConfirmation'::bytea in body) > 0`. The tests/CLAUDE.md note (Task 4 Step 5) says so. |

- [ ] **Step 5: Stop if a finding breaks the design**

If V3 or V6 fails (Retry does not re-deliver, or scheduling does not work for RabbitMQ-bound
messages), stop and bring the finding to the owner: the jobs half of the spec depends on both.

- [ ] **Step 6: Delete the probe and commit**

```bash
git rm -r tests/Worker.IntegrationTests/Probe
git add src/Infrastructure/AiFramework.Infrastructure.csproj tests/*/AiFramework.*.csproj docs/superpowers/
git commit -m "build(messaging): add WolverineFx.RabbitMQ and record the verified RabbitMQ API"
```

(The probe file never lands in `main`; it exists only in this task's working tree.)

---

## Task 2: The local broker (compose, dev settings, local-run scripts)

**Files:**
- Modify: `docker-compose.yml`, `docker-compose.e2e.yml`
- Modify: `src/Api/appsettings.Development.json`, `src/Worker/appsettings.Development.json`
- Modify: `scripts/dev.ps1`, `scripts/worker.ps1`, `local-run/control-panel.bat`
- Modify: `frontend/e2e/support/env.ts`, `frontend/playwright.config.ts`
- Modify: `.claude/skills/local-dev/SKILL.md`, `docs/local-development.md`

**Interfaces:**
- Produces: a broker at `amqp://aiframework:aiframework@localhost:55672/` whenever the dev loop
  runs; `E2E_RABBITMQ_URL`, `E2E_RABBITMQ_UI_URL` exported from `frontend/e2e/support/env.ts`.

- [ ] **Step 1: Add the dev broker to `docker-compose.yml`**

After the `postgres` service:

```yaml
  # The message broker for all asynchronous work: job lanes, events for other systems, and
  # messages from them (ADR 0026). In the DEFAULT profile, unlike Seq and Redis: the API and the
  # worker refuse to start without it, so it must come up with a bare `docker compose up`.
  rabbitmq:
    image: rabbitmq:4.3-management-alpine
    # Load-bearing. RabbitMQ keys its data directory on the node name (rabbit@<hostname>), and
    # compose's default hostname is the container id - a new one on every recreate, which would
    # orphan every message in the named volume below.
    hostname: rabbitmq
    environment:
      # Not "guest": the guest user may only connect from localhost INSIDE the container, and a
      # connection arriving through the port mapping does not count. Throwaway credentials for a
      # localhost-only container, the same judgement as the dev Postgres password above.
      RABBITMQ_DEFAULT_USER: aiframework
      RABBITMQ_DEFAULT_PASS: aiframework
    ports:
      - '${RABBITMQ_PORT:-55672}:5672'
      - '${RABBITMQ_UI_PORT:-55673}:15672'
    healthcheck:
      # check_port_connectivity, not ping: ping answers as soon as the node is up, before the AMQP
      # listener accepts connections - and the API refuses to start if it connects too early.
      test: ['CMD', 'rabbitmq-diagnostics', '-q', 'check_port_connectivity']
      interval: 2s
      timeout: 5s
      retries: 60
    volumes:
      # Named, like pgdata: queued messages survive a `down`. `down -v` is the reset.
      - rabbitmqdata:/var/lib/rabbitmq
```

and add `rabbitmqdata:` under the top-level `volumes:`.

- [ ] **Step 2: Add the e2e broker to `docker-compose.e2e.yml`**

```yaml
  # Throwaway, like the e2e Postgres: no volume, its own ports, so it can run beside the dev one.
  rabbitmq:
    image: rabbitmq:4.3-management-alpine
    hostname: rabbitmq
    environment:
      RABBITMQ_DEFAULT_USER: e2e
      RABBITMQ_DEFAULT_PASS: e2e
    ports:
      - '${E2E_RABBITMQ_PORT:-55682}:5672'
      # The management API is how e2e/support/broker.ts publishes a shipment - no AMQP client
      # dependency in the frontend workspace.
      - '${E2E_RABBITMQ_UI_PORT:-55683}:15672'
    healthcheck:
      test: ['CMD', 'rabbitmq-diagnostics', '-q', 'check_port_connectivity']
      interval: 2s
      timeout: 5s
      retries: 60
```

- [ ] **Step 3: Dev connection strings**

In both `src/Api/appsettings.Development.json` and `src/Worker/appsettings.Development.json`, add
to `ConnectionStrings` (keep the existing comment in the Api file, extend it with one line):

```jsonc
    // The dev broker in docker-compose.yml; throwaway credentials, same judgement as Default.
    "RabbitMq": "amqp://aiframework:aiframework@localhost:55672/"
```

- [ ] **Step 4: Point the e2e servers at the e2e broker**

In `frontend/e2e/support/env.ts`, after `E2E_CONNECTION_STRING`:

```ts
export const E2E_RABBITMQ_PORT = process.env.E2E_RABBITMQ_PORT ?? '55682';
export const E2E_RABBITMQ_UI_PORT = process.env.E2E_RABBITMQ_UI_PORT ?? '55683';
export const E2E_RABBITMQ_URL = `amqp://e2e:e2e@localhost:${E2E_RABBITMQ_PORT}/`;
/** The management HTTP API, which e2e/support/broker.ts publishes through. */
export const E2E_RABBITMQ_UI_URL = `http://localhost:${E2E_RABBITMQ_UI_PORT}`;
```

In `frontend/playwright.config.ts`, import `E2E_RABBITMQ_URL` and add to **both** the API and the
worker `webServer.env` blocks:

```ts
              // The e2e broker in docker-compose.e2e.yml. Both hosts refuse to start without one
              // (ADR 0026): the API publishes, the worker publishes and listens.
              ConnectionStrings__RabbitMq: E2E_RABBITMQ_URL,
```

- [ ] **Step 5: `scripts/dev.ps1`**

Beside `$pgPort`:

```powershell
# Same defaults docker-compose.yml's RABBITMQ_PORT / RABBITMQ_UI_PORT fall back to.
$rabbitPort = if ($env:RABBITMQ_PORT) { $env:RABBITMQ_PORT } else { '55672' }
$rabbitUiPort = if ($env:RABBITMQ_UI_PORT) { $env:RABBITMQ_UI_PORT } else { '55673' }
```

The `docker compose up -d --wait` step needs no change — `--wait` now also blocks on the broker's
healthcheck, and that ordering is what the API and worker need. Rename its step title to
`'Starting the dev database and message broker'`.

Where the script sets `$env:ConnectionStrings__Default` before spawning the API (and again for
the worker), set the broker string the same way, and remove it again at the same point the
existing code removes `ConnectionStrings__Default`:

```powershell
        $env:ConnectionStrings__RabbitMq = "amqp://aiframework:aiframework@localhost:$rabbitPort/"
```

Extend the ready banner:

```powershell
Write-Host "  RabbitMQ         amqp://localhost:$rabbitPort   UI http://localhost:$rabbitUiPort  (aiframework / aiframework)" -ForegroundColor Green
```

and change the last DarkGray line to `'  scripts\stop-dev.ps1 stops the database and broker (and Seq, if it was started).'`.

- [ ] **Step 6: `scripts/worker.ps1`**

The existing `docker compose up -d --wait` already starts the broker. Rename its step title to
`'Starting the dev database and message broker'`, update the `.DESCRIPTION` sentence "against the
dev database" to "against the dev database and message broker", and — for `-SkipDatabase` — add
before the worker port check:

```powershell
if ($SkipDatabase) {
    $rabbitPort = if ($env:RABBITMQ_PORT) { [int]$env:RABBITMQ_PORT } else { 55672 }
    if (-not (Get-NetTCPConnection -LocalPort $rabbitPort -State Listen -ErrorAction SilentlyContinue)) {
        # Plain ASCII, as dev.ps1's messages: PowerShell 5.1 mangles an em dash.
        throw "Nothing is listening on the message broker port ($rabbitPort). The worker refuses to start without it - run without -SkipDatabase, or start the dev loop first."
    }
}
```

- [ ] **Step 7: `local-run/control-panel.bat`**

Change menu line 2 to:

```bat
echo   2. Start dev loop        (dev Postgres + RabbitMQ + API + job worker + Vite, scripts\dev.ps1)
```

and line 5 to `(scripts\stop-dev.ps1 - stops the database and broker, and Seq if it was started)`.

- [ ] **Step 8: Verify the broker by hand**

```bash
docker compose up -d --wait
curl -s -u aiframework:aiframework http://localhost:55673/api/overview | grep -o '"rabbitmq_version":"[^"]*"'
docker compose -f docker-compose.e2e.yml -p aiframework-e2e up -d --wait
curl -s -u e2e:e2e http://localhost:55683/api/overview | grep -o '"rabbitmq_version":"[^"]*"'
docker compose -f docker-compose.e2e.yml -p aiframework-e2e down
```

Expected: `"rabbitmq_version":"4.3.x"` twice.

- [ ] **Step 9: Docs**

`.claude/skills/local-dev/SKILL.md`: add the broker to the two-databases paragraph (dev 55672/55673,
e2e 55682/55683), note that `dotnet run` alone (Rider/F5) needs `docker compose up -d` first
because both hosts refuse to start without the broker, and add `Start dev loop` → "Postgres,
**RabbitMQ**, API, job worker, Vite" in the menu table. `docs/local-development.md`: the same
prerequisite for IDE startup.

- [ ] **Step 10: Commit**

```bash
git add docker-compose.yml docker-compose.e2e.yml src/*/appsettings.Development.json scripts/ local-run/ frontend/e2e/support/env.ts frontend/playwright.config.ts .claude/skills/local-dev/SKILL.md docs/local-development.md
git commit -m "feat(messaging): run a RabbitMQ broker in the dev loop and the e2e stack"
```

---

## Task 3: Transport wiring and the startup contract

**Files:**
- Create: `src/Infrastructure/EventPath/RabbitMqTopology.cs`
- Modify: `src/Infrastructure/EventPath/WolverineEventPath.cs`
- Modify: `src/Api/Program.cs`, `src/Worker/Program.cs`
- Modify: `tests/Api.IntegrationTests/ApiFactory.cs`, `tests/Worker.IntegrationTests/WorkerFactory.cs`
- Create: `tests/Api.IntegrationTests/Messaging/BrokerProbe.cs`,
  `tests/Worker.IntegrationTests/Messaging/BrokerProbe.cs`
- Create: `tests/Api.IntegrationTests/Messaging/TopologyTests.cs`

**Interfaces:**
- Produces: `RabbitMqTopology` constants (`EventsExchange`, `UnroutedExchange`, `UnroutedQueue`,
  `ShipmentsQueue`, `UnroutedMaxLength`); `RabbitMqTopology.Declare(RabbitMqTransportExpression)`;
  `AddWolverineEventPath(..., string? rabbitMqConnectionString, ...)`; `ApiFactory.RabbitMqConnectionString`,
  `WorkerFactory.RabbitMqConnectionString`; `BrokerProbe` (see Step 3).

- [ ] **Step 1: Start a broker in both test factories**

`tests/Api.IntegrationTests/ApiFactory.cs`:

```csharp
    private readonly RabbitMqContainer _rabbit =
        new RabbitMqBuilder("rabbitmq:4.3-management-alpine").Build();

    /// <summary>For tests that inspect or publish to the broker directly (BrokerProbe).</summary>
    public string RabbitMqConnectionString => _rabbit.GetConnectionString();
```

Start both containers together — `await Task.WhenAll(_container.StartAsync(), _rabbit.StartAsync());`
— in `InitializeAsync`, before `Services` is touched; in `DisposeAsync`, after
`base.DisposeAsync()` (host first, then containers — see the comment already there):

```csharp
        await base.DisposeAsync();
        await _container.DisposeAsync();
        await _rabbit.DisposeAsync();
```

In `ConfigureWebHost`, beside the Postgres setting:

```csharp
        // Durable Wolverine now also connects to RabbitMQ while the host starts (ADR 0026), so the
        // broker, like the database, must be supplied before the host is built.
        builder.UseSetting("ConnectionStrings:RabbitMq", _rabbit.GetConnectionString());
```

Make the identical three changes to `tests/Worker.IntegrationTests/WorkerFactory.cs` (its
`InitializeAsync` migrates a standalone DbContext; start the broker in the same `Task.WhenAll`
before that migration).

- [ ] **Step 2: Run the suites to see the baseline still passes with an unused broker**

Run: `dotnet test tests/Api.IntegrationTests tests/Worker.IntegrationTests --nologo`
Expected: all pass (the broker is started but nothing uses it yet).

- [ ] **Step 3: Write `BrokerProbe` (both test projects)**

`tests/Api.IntegrationTests/Messaging/BrokerProbe.cs` (and an identical copy in
`tests/Worker.IntegrationTests/Messaging/` with that project's namespace — the two assemblies share
no test library, and this is ~70 lines):

```csharp
using System.Text;
using RabbitMQ.Client;

namespace AiFramework.Api.IntegrationTests.Messaging;

/// <summary>
/// Reads and writes the test broker directly, the way an external system would: RabbitMQ.Client,
/// no Wolverine. Every queue it creates is server-named, exclusive and auto-delete, so tests in
/// one collection never see each other's messages and nothing outlives the connection.
/// </summary>
internal sealed class BrokerProbe : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;

    private BrokerProbe(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public static async Task<BrokerProbe> ConnectAsync(string connectionString)
    {
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        var connection = await factory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        return new BrokerProbe(connection, channel);
    }

    /// <summary>A private queue bound to <paramref name="exchange"/> with <paramref name="routingKey"/>.</summary>
    public async Task<string> BindTemporaryQueueAsync(string exchange, string routingKey)
    {
        var declared = await _channel.QueueDeclareAsync(
            queue: string.Empty, durable: false, exclusive: true, autoDelete: true);
        await _channel.QueueBindAsync(declared.QueueName, exchange, routingKey);
        return declared.QueueName;
    }

    /// <summary>
    /// The next message on <paramref name="queue"/>, waiting up to <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// A bounded real-time wait, deliberately — the one place these tests poll. Delivery is done by
    /// another process (the broker) after Wolverine's own sending agent runs, neither of which an
    /// IClock reaches. It returns as soon as a message is there, so only a failing test pays the
    /// timeout; the same trade ExchangeRateClientTests documents.
    /// </remarks>
    public async Task<BasicGetResult?> WaitForMessageAsync(string queue, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var result = await _channel.BasicGetAsync(queue, autoAck: true);
            if (result is not null || DateTime.UtcNow > deadline)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>Publishes a raw JSON body straight to a queue, as a producer with no Wolverine would.</summary>
    public async Task PublishToQueueAsync(string queue, string json, string? messageId = null)
    {
        var properties = new BasicProperties { ContentType = "application/json", Persistent = true };
        if (messageId is not null)
        {
            properties.MessageId = messageId;
        }

        await _channel.BasicPublishAsync(
            exchange: string.Empty, routingKey: queue, mandatory: true,
            basicProperties: properties, body: Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Throws if the object does not exist - which is what a topology test wants.</summary>
    public Task ExchangeExistsAsync(string exchange) => _channel.ExchangeDeclarePassiveAsync(exchange);

    public async Task<uint> MessageCountAsync(string queue) =>
        (await _channel.QueueDeclarePassiveAsync(queue)).MessageCount;

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
```

If Task 1 recorded different RabbitMQ.Client 7 method names, use those.

- [ ] **Step 4: Write the failing topology test**

`tests/Api.IntegrationTests/Messaging/TopologyTests.cs`:

```csharp
using AiFramework.Infrastructure.EventPath;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;

namespace AiFramework.Api.IntegrationTests.Messaging;

/// <summary>What the API declares on the broker as it starts. ADR 0026.</summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class TopologyTests(ApiFactory factory)
{
    [Theory]
    [InlineData(RabbitMqTopology.EventsExchange)]
    [InlineData(RabbitMqTopology.UnroutedExchange)]
    public async Task Startup_DeclaresTheExchange(string exchange)
    {
        _ = factory.Services; // builds and starts the host, which provisions the topology
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);

        var act = () => probe.ExchangeExistsAsync(exchange);

        await act.Should().NotThrowAsync($"{exchange} is declared by AutoProvision at startup");
    }

    [Fact]
    public async Task Startup_DeclaresTheUnroutedQueue()
    {
        _ = factory.Services;
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);

        var act = () => probe.MessageCountAsync(RabbitMqTopology.UnroutedQueue);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TheApi_ListensOnNoRabbitMqQueue()
    {
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();
        var capabilities = await ServiceCapabilities.ReadFrom(
            runtime, new Uri("local://topology-test"), CancellationToken.None);

        var rabbitListeners = capabilities.MessagingEndpoints
            .Where(e => e.IsListener && e.Uri.Scheme == "rabbitmq")
            .Select(e => e.Uri.ToString())
            .ToList();

        rabbitListeners.Should().BeEmpty("the API listens to no queue (ADR 0016); only the worker does");
    }
}
```

Add to the same class the guard's test — a durable host with no broker configured must refuse to
start, loudly:

```csharp
    [Fact]
    public void Startup_WithoutARabbitMqConnectionString_Refuses()
    {
        using var misconfigured = factory.WithWebHostBuilder(
            builder => builder.UseSetting("ConnectionStrings:RabbitMq", string.Empty));

        var act = () => misconfigured.Services;

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionStrings:RabbitMq*");
    }
```

(Add `using Microsoft.AspNetCore.Hosting;` for `UseSetting`.)

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~TopologyTests"`
Expected: FAIL — `RabbitMqTopology` does not exist.

- [ ] **Step 5: Create `RabbitMqTopology`**

`src/Infrastructure/EventPath/RabbitMqTopology.cs` (use the exact API Task 1 recorded for V1):

```csharp
using Wolverine.RabbitMQ;

namespace AiFramework.Infrastructure.EventPath;

/// <summary>
/// Every name this application gives an object on the broker, and the one place they are declared.
/// See ADR 0026 and the messaging skill.
/// </summary>
/// <remarks>
/// Names are dotted, RabbitMQ's convention - unlike the Postgres queue transport, which rewrote a
/// hyphen into an underscore, RabbitMQ keeps a name exactly as given.
/// </remarks>
public static class RabbitMqTopology
{
    /// <summary>Every event we publish. Topic; routing key = the domain event's registered name.</summary>
    public const string EventsExchange = "aiframework.events";

    /// <summary>
    /// The alternate exchange of <see cref="EventsExchange"/>. RabbitMQ DROPS a message published to
    /// an exchange with no matching binding; with no consumer yet, that would be every event. This
    /// catches them instead.
    /// </summary>
    public const string UnroutedExchange = "aiframework.events.unrouted";

    /// <summary>Where unrouted events wait. Capped so nobody reading it cannot fill a disk.</summary>
    public const string UnroutedQueue = "aiframework.events.unrouted";

    public const int UnroutedMaxLength = 100_000;

    /// <summary>Inbound shipment confirmations. Producers publish to it by name (default exchange).</summary>
    public const string ShipmentsQueue = "aiframework.shipments";

    /// <summary>Declares the exchanges and the unrouted queue. Job and shipment queues are
    /// declared by their own routes and listeners.</summary>
    public static void Declare(RabbitMqTransportExpression rabbit)
    {
        ArgumentNullException.ThrowIfNull(rabbit);

        rabbit.DeclareExchange(UnroutedExchange, exchange =>
        {
            exchange.ExchangeType = ExchangeType.Fanout;
            exchange.BindQueue(UnroutedQueue);
        });

        rabbit.DeclareQueue(UnroutedQueue, queue =>
        {
            queue.QueueType = QueueType.quorum;
            queue.Arguments["x-max-length"] = UnroutedMaxLength;
            // Oldest first: when nobody reads it, the most recent events are the useful ones.
            queue.Arguments["x-overflow"] = "drop-head";
        });

        rabbit.DeclareExchange(EventsExchange, exchange =>
        {
            exchange.ExchangeType = ExchangeType.Topic;
            exchange.Arguments["alternate-exchange"] = UnroutedExchange;
        });
    }
}
```

- [ ] **Step 6: Wire RabbitMQ into the durable branch**

In `src/Infrastructure/EventPath/WolverineEventPath.cs`:

1. Add a parameter after `connectionString`, and document it:

```csharp
    /// <param name="rabbitMqConnectionString">
    /// The broker's AMQP URI. Required whenever <paramref name="durable"/> is true - RabbitMQ is
    /// configured inside the durable branch, so <c>Wolverine__Durable=false</c> (codegen, the
    /// OpenAPI contract, HealthTests) turns it off with the Postgres transport and needs no switch
    /// of its own. ADR 0026.
    /// </param>
        string? rabbitMqConnectionString,
```

2. Thread it through `ConfigureTransport(opts, connectionString, rabbitMqConnectionString, role, jobOptions, durable)`.

3. In `ConfigureTransport`, after `ConfigureDurability(opts, connectionString);` and **before**
   `ConfigureJobs(...)` (job routes need the transport):

```csharp
        ConfigureRabbitMq(opts, rabbitMqConnectionString, role);
```

4. Add:

```csharp
    /// <summary>The broker, per ADR 0026. Only ever reached when durable.</summary>
    private static void ConfigureRabbitMq(
        WolverineOptions opts, string? rabbitMqConnectionString, WolverineHostRole role)
    {
        // Loud, like Program.cs's ConnectionStrings:Default guard. A durable host with no broker
        // configured is a misconfiguration, and starting "healthy" with no transport would queue
        // every job into nowhere.
        if (string.IsNullOrWhiteSpace(rabbitMqConnectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:RabbitMq is required when Wolverine is durable. Set " +
                "ConnectionStrings__RabbitMq (double underscores), or Wolverine__Durable=false for a " +
                "host that must start without infrastructure.");
        }

        var rabbit = opts.UseRabbitMq(new Uri(rabbitMqConnectionString))
            // Declares what the routes and listeners name, at startup. Needs the broker reachable,
            // which is the fail-fast contract: an unreachable broker stops the host (spec section 6).
            .AutoProvision()
            // Failures go to Wolverine's Postgres dead-letter storage, which the monitoring page
            // lists and retries - never to a RabbitMQ-native DLQ nobody would look at.
            .DisableDeadLetterQueueing()
            .UseQuorumQueues()
            .ConfigureChannelCreation(channel =>
            {
                channel.PublisherConfirmationsEnabled = true;
                channel.PublisherConfirmationTrackingEnabled = true;
            });

        // The API only sends. A sender-only connection is the transport-level half of
        // ApiPublishesOnlyTests: there is no listening connection to attach a listener to.
        if (role is WolverineHostRole.PublishesJobs)
        {
            rabbit.UseSenderConnectionOnly();
        }

        RabbitMqTopology.Declare(rabbit);

        // Every sending endpoint durable: a job or an event is written to Postgres before the broker
        // sees it, and a broker outage only delays it.
        opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
        opts.Policies.UseDurableInboxOnAllListeners();
    }
```

(Replace `UseQuorumQueues()` / `UseSenderConnectionOnly()` with the V1 finding if they differ.)

- [ ] **Step 7: Pass the connection string from both hosts**

`src/Api/Program.cs`, in the `AddWolverineEventPath(` call after `connectionString,`:

```csharp
    // The broker (ADR 0026). Null is allowed here and refused inside the durable branch, so a host
    // started with Wolverine__Durable=false needs no broker at all.
    builder.Configuration.GetConnectionString("RabbitMq"),
```

`src/Worker/Program.cs`: the same line in its call.

- [ ] **Step 8: Run the topology tests**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~TopologyTests"`
Expected: PASS (3 facts + 2 theory rows).

- [ ] **Step 9: Prove the durable=false paths stay broker-free**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~HealthTests|FullyQualifiedName~OpenApiDocumentTests"`
Expected: PASS — neither sets `ConnectionStrings:RabbitMq`.

Then the contract generation incantation from the regenerate skill (no RabbitMQ variable):

```bash
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' Wolverine__Durable=false Admin__ReconcileOnStart=false dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
git diff --exit-code -- openapi/
```

Expected: exit 0.

- [ ] **Step 10: Full backend run, then commit**

Run: `dotnet test --nologo -v q`
Expected: all pass.

```bash
git add src/Infrastructure/EventPath src/Api/Program.cs src/Worker/Program.cs tests/
git commit -m "feat(messaging): connect both hosts to RabbitMQ and declare the event topology"
```

---

## Task 4: Job lanes on RabbitMQ

**Files:**
- Modify: `src/Infrastructure/Jobs/JobRegistration.cs`
- Modify: `tests/Worker.IntegrationTests/Jobs/JobDeliveryTests.cs`
- Modify: `src/Api/Internal/Generated/**`, `src/Worker/Internal/Generated/**` (regenerated)
- Modify: `tests/CLAUDE.md`, `.claude/skills/jobs/SKILL.md`

**Interfaces:**
- Consumes: the durable branch's RabbitMQ transport (Task 3).
- Produces: `JobRegistration.QueueFor(JobLane)` returning `aiframework.jobs.light` /
  `aiframework.jobs.heavy`.

- [ ] **Step 1: Re-point the scheduled-job test (it will fail)**

Replace `AScheduledJob_IsHeldInTheLanesScheduledTable` in `JobDeliveryTests.cs` with the V6
finding. With the expected answer (Wolverine holds a scheduled send in its incoming envelope table
with status `Scheduled`) it is:

```csharp
    /// <summary>
    /// A scheduled job is held DURABLY, not run now - the recurring-job pattern depends on it.
    /// </summary>
    /// <remarks>
    /// RabbitMQ has no delayed delivery; Wolverine holds the envelope in its own Postgres storage
    /// until it is due and only then sends it to the lane's queue. Verified in the plan's Task 1
    /// (V6). Read directly rather than through a tracking session, which would only time out.
    /// </remarks>
    [Fact]
    public async Task AScheduledJob_IsHeldInWolverinesStorageUntilDue()
    {
        var job = new SendOrderConfirmation(Guid.NewGuid(), "SKU-JOB-LATER", 1);
        await ScheduleAsync(job, TimeSpan.FromHours(1));

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);

        await using var command = new NpgsqlCommand(
            "select count(*) from wolverine.wolverine_incoming_envelopes " +
            "where status = 'Scheduled' and message_type like '%SendOrderConfirmation%'",
            connection);

        var scheduled = Convert.ToInt64(
            await command.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture);

        scheduled.Should().BeGreaterThan(0,
            "a job scheduled an hour out must wait in Wolverine's storage - missing means it ran " +
            "immediately or was dropped, and a recurring job would spin or stop");
    }
```

Run: `dotnet test tests/Worker.IntegrationTests --filter "FullyQualifiedName~JobDeliveryTests"`
Expected: FAIL — jobs still ride Postgres queues, so nothing is `Scheduled` in that table.

- [ ] **Step 2: Move routing and listening to RabbitMQ**

In `JobDescriptor.RouteFor<TJob>()`:

```csharp
        static opts => opts.PublishMessage<TJob>()
            .ToRabbitQueue(JobRegistration.QueueFor(TJob.Lane));
```

In `JobRegistration.QueueFor`, and replace its `<remarks>` with one sentence ("Dotted, RabbitMQ's
naming convention; RabbitMQ keeps a queue name exactly as given."):

```csharp
        JobLane.Light => "aiframework.jobs.light",
        JobLane.Heavy => "aiframework.jobs.heavy",
```

In `ListenForJobs`:

```csharp
            opts.ListenToRabbitQueue(QueueFor(lane))
                .MaximumParallelMessages(options.ParallelismFor(lane));
```

Swap `using Wolverine.Postgresql;` for `using Wolverine.RabbitMQ;` in this file (the Postgres
package stays referenced — it is the envelope storage).

- [ ] **Step 3: Run the job tests**

Run: `dotnet test tests/Worker.IntegrationTests tests/Api.IntegrationTests --filter "FullyQualifiedName~Job"`
Expected: PASS — `TheWorker_ListensOnEveryLane`, `ALightJob_ReachesItsHandler`,
`AUserScopedJob_ResolvesItsOwnerAsTheCurrentUser`, the new scheduled test, and all three
`ApiPublishesOnlyTests` (they match on `QueueFor`, so they follow the rename unchanged).

- [ ] **Step 4: Regenerate both Wolverine trees**

```bash
export ConnectionStrings__Default='Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder' Wolverine__Durable=false
dotnet run --project src/Api -- codegen write
dotnet run --project src/Worker -- codegen write
git status --short src/Api/Internal src/Worker/Internal
```

Keep files whose diff is only line endings or reordering inside handlers you did not change
(`git checkout --` them). Then:
Run: `dotnet test --nologo -v q --filter "FullyQualifiedName~Codegen"`
Expected: PASS.

- [ ] **Step 5: Docs**

`tests/CLAUDE.md`, "Worker tests": replace "A scheduled job waits in
`wolverine_queues.wolverine_queue_<lane>_scheduled` …" with "A scheduled job waits in
`wolverine.wolverine_incoming_envelopes` with status `Scheduled` until due; RabbitMQ has no delayed
delivery." `.claude/skills/jobs/SKILL.md`: queues are RabbitMQ quorum queues
`aiframework.jobs.{light,heavy}`; the "RabbitMQ is deferred with named triggers" line becomes
"Jobs ride RabbitMQ (ADR 0026); Postgres still holds the envelope storage".

- [ ] **Step 6: Commit**

```bash
git add src/Infrastructure/Jobs src/*/Internal/Generated tests/Worker.IntegrationTests tests/CLAUDE.md .claude/skills/jobs/SKILL.md
git commit -m "feat(messaging): move the job lanes from Postgres queues to RabbitMQ"
```

---

## Task 5: Integration contracts and their wire shape

**Files:**
- Create: `src/Application/IntegrationEvents/IIntegrationEvent.cs`,
  `src/Application/IntegrationEvents/OutboundContracts.cs`,
  `src/Application/IntegrationEvents/ShipmentConfirmedV1.cs`
- Create: `src/Infrastructure/Integration/IntegrationJson.cs`
- Create: `tests/Infrastructure.Tests/Integration/IntegrationJsonTests.cs`

**Interfaces:**
- Produces:
  - `interface IIntegrationEvent { Guid EventId { get; } DateTimeOffset OccurredAt { get; } static abstract string TypeName { get; } static abstract string RoutingKey { get; } }`
  - `OrderPlacedV1(Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid BuyerId, string Sku, int Quantity, string? ProductName, decimal? UnitPrice)`
  - `OrderShippedV1(Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid BuyerId, string Sku)`
  - `OrderCancelledV1(Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid BuyerId, string Sku, string Reason)`
  - `ProductPriceChangedV1(Guid EventId, DateTimeOffset OccurredAt, Guid ProductId, string Sku, string Name, decimal OldPrice, decimal NewPrice)`
  - `ShipmentConfirmedV1(string ShipmentId, Guid OrderId, DateTimeOffset ShippedAt)` with
    `const string TypeName = "shipment.confirmed.v1"`
  - `IntegrationJson.Options` (`JsonSerializerOptions`)

- [ ] **Step 1: Write the failing wire-shape tests**

`tests/Infrastructure.Tests/Integration/IntegrationJsonTests.cs`:

```csharp
using System.Text.Json;
using AiFramework.Application.IntegrationEvents;
using AiFramework.Infrastructure.Integration;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Integration;

/// <summary>
/// The wire contract external systems depend on. A change that breaks one of these is a breaking
/// change to them - which is the point of pinning the shape here. See the messaging skill.
/// </summary>
public sealed class IntegrationJsonTests
{
    private static readonly Guid EventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BuyerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset At = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OrderPlacedV1_SerializesAsCamelCaseJson()
    {
        var json = JsonSerializer.Serialize(
            new OrderPlacedV1(EventId, At, OrderId, BuyerId, "SKU-1", 2, "Widget", 19.95m),
            IntegrationJson.Options);

        json.Should().Be(
            "{\"eventId\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"occurredAt\":\"2026-09-27T12:00:00+00:00\"," +
            "\"orderId\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"buyerId\":\"33333333-3333-3333-3333-333333333333\"," +
            "\"sku\":\"SKU-1\",\"quantity\":2,\"productName\":\"Widget\",\"unitPrice\":19.95}");
    }

    [Fact]
    public void OrderPlacedV1_WithNoProductSnapshot_WritesNulls()
    {
        var json = JsonSerializer.Serialize(
            new OrderPlacedV1(EventId, At, OrderId, BuyerId, "SKU-1", 2, null, null),
            IntegrationJson.Options);

        json.Should().Contain("\"productName\":null").And.Contain("\"unitPrice\":null");
    }

    [Fact]
    public void ShipmentConfirmedV1_FromCamelCase_Deserializes()
    {
        var message = JsonSerializer.Deserialize<ShipmentConfirmedV1>(
            "{\"shipmentId\":\"WH-1\",\"orderId\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"shippedAt\":\"2026-09-27T12:00:00+00:00\"}",
            IntegrationJson.Options);

        message.Should().Be(new ShipmentConfirmedV1("WH-1", OrderId, At));
    }

    // Review Focus 1: an external producer is not ours to format.
    [Fact]
    public void ShipmentConfirmedV1_FromPascalCaseWithExtraFields_Deserializes()
    {
        var message = JsonSerializer.Deserialize<ShipmentConfirmedV1>(
            "{\"ShipmentId\":\"WH-1\",\"OrderId\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"ShippedAt\":\"2026-09-27T12:00:00+00:00\",\"Carrier\":\"DHL\"}",
            IntegrationJson.Options);

        message.Should().Be(new ShipmentConfirmedV1("WH-1", OrderId, At));
    }

    [Theory]
    [InlineData(typeof(OrderPlacedV1), "order.placed.v1", "order.placed")]
    [InlineData(typeof(OrderShippedV1), "order.shipped.v1", "order.shipped")]
    [InlineData(typeof(OrderCancelledV1), "order.cancelled.v1", "order.cancelled")]
    [InlineData(typeof(ProductPriceChangedV1), "product.price_changed.v1", "product.price_changed")]
    public void EachOutboundContract_HasItsVersionedNameAndRoutingKey(
        Type contract, string typeName, string routingKey)
    {
        var names = (ValueTuple<string, string>)typeof(IntegrationJsonTests)
            .GetMethod(nameof(NamesOf), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(contract)
            .Invoke(null, null)!;

        names.Should().Be((typeName, routingKey));
    }

    // Reads the static abstract members through a generic, the only way to reach them.
    private static (string, string) NamesOf<T>() where T : IIntegrationEvent => (T.TypeName, T.RoutingKey);
}
```

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~IntegrationJsonTests"`
Expected: FAIL — the types do not exist.

- [ ] **Step 2: The contracts (Application)**

`src/Application/IntegrationEvents/IIntegrationEvent.cs`:

```csharp
namespace AiFramework.Application.IntegrationEvents;

/// <summary>
/// An event published to OTHER systems. Deliberately separate from <c>IDomainEvent</c>: a domain
/// event may change shape freely; an integration event is a contract someone else depends on.
/// </summary>
/// <remarks>
/// Versioning: fields may be ADDED within a version. Renaming or removing one means a new type
/// (<c>…V2</c>, <c>"….v2"</c>) published alongside the old until consumers move. ADR 0026.
/// </remarks>
public interface IIntegrationEvent
{
    /// <summary>The hand-built outbox's MessageId: stable across redelivery, the consumer's dedupe key.</summary>
    public Guid EventId { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>The AMQP <c>type</c> property, e.g. <c>order.placed.v1</c>.</summary>
    public static abstract string TypeName { get; }

    /// <summary>The routing key on <c>aiframework.events</c>, e.g. <c>order.placed</c>.</summary>
    public static abstract string RoutingKey { get; }
}
```

`src/Application/IntegrationEvents/OutboundContracts.cs`:

```csharp
namespace AiFramework.Application.IntegrationEvents;

/// <summary><c>order.placed.v1</c>. ProductName/UnitPrice are null for orders that predate the catalogue.</summary>
public sealed record OrderPlacedV1(
    Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid BuyerId, string Sku, int Quantity,
    string? ProductName, decimal? UnitPrice) : IIntegrationEvent
{
    public static string TypeName => "order.placed.v1";

    public static string RoutingKey => "order.placed";
}

/// <summary><c>order.shipped.v1</c>.</summary>
public sealed record OrderShippedV1(
    Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid BuyerId, string Sku) : IIntegrationEvent
{
    public static string TypeName => "order.shipped.v1";

    public static string RoutingKey => "order.shipped";
}

/// <summary><c>order.cancelled.v1</c>, carrying the buyer's reason.</summary>
public sealed record OrderCancelledV1(
    Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid BuyerId, string Sku, string Reason)
    : IIntegrationEvent
{
    public static string TypeName => "order.cancelled.v1";

    public static string RoutingKey => "order.cancelled";
}

/// <summary><c>product.price_changed.v1</c>.</summary>
public sealed record ProductPriceChangedV1(
    Guid EventId, DateTimeOffset OccurredAt, Guid ProductId, string Sku, string Name,
    decimal OldPrice, decimal NewPrice) : IIntegrationEvent
{
    public static string TypeName => "product.price_changed.v1";

    public static string RoutingKey => "product.price_changed";
}
```

`src/Application/IntegrationEvents/ShipmentConfirmedV1.cs`:

```csharp
namespace AiFramework.Application.IntegrationEvents;

/// <summary>
/// <c>shipment.confirmed.v1</c>, received on <c>aiframework.shipments</c> from a warehouse.
/// </summary>
/// <param name="ShipmentId">The warehouse's own reference - a string, because it is theirs.</param>
public sealed record ShipmentConfirmedV1(string ShipmentId, Guid OrderId, DateTimeOffset ShippedAt)
{
    public const string TypeName = "shipment.confirmed.v1";
}
```

- [ ] **Step 3: The serializer options (Infrastructure)**

`src/Infrastructure/Integration/IntegrationJson.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiFramework.Infrastructure.Integration;

/// <summary>
/// The one JSON shape for everything on the broker that another system reads or writes: web
/// defaults (camelCase out, case-insensitive in, unknown members ignored) and enums as names -
/// the same shape the HTTP API already uses.
/// </summary>
public static class IntegrationJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~IntegrationJsonTests"`
Expected: PASS (5 facts + 4 theory rows). If `OccurredAt` renders differently (e.g. `Z`), fix the
expected string to what System.Text.Json writes for `DateTimeOffset` with a zero offset — the
test pins the real shape, it does not choose it.

- [ ] **Step 5: Commit**

```bash
git add src/Application/IntegrationEvents src/Infrastructure/Integration/IntegrationJson.cs tests/Infrastructure.Tests/Integration
git commit -m "feat(messaging): versioned integration contracts and their JSON shape"
```

---

## Task 6: Outbound publishing

**Files:**
- Modify: `src/Application/Abstractions/DomainEventHandling.cs`
- Modify: `src/Infrastructure/Outbox/OutboxWorkItemProcessor.cs` (+ the work item type it reads)
- Create: `src/Application/IntegrationEvents/IIntegrationEventPublisher.cs`,
  `src/Application/IntegrationEvents/IntegrationPublishers.cs`
- Modify: `src/Application/Orders/IOrderRepository.cs`, `src/Infrastructure/Persistence/OrderRepository.cs`
- Create: `src/Infrastructure/Integration/IntegrationEventRegistration.cs`,
  `src/Infrastructure/Integration/IntegrationEnvelopeMapper.cs`,
  `src/Infrastructure/Integration/WolverineIntegrationEventPublisher.cs`
- Modify: `src/Infrastructure/EventPath/WolverineEventPath.cs`, `src/Infrastructure/InfrastructureRegistration.cs`
- Create: `tests/Application.Tests/IntegrationEvents/IntegrationPublisherTests.cs`,
  `tests/Infrastructure.Tests/Integration/IntegrationEventRegistrationTests.cs`,
  `tests/Api.IntegrationTests/Messaging/OutboundEventTests.cs`

**Interfaces:**
- Consumes: Task 5 contracts; `RabbitMqTopology.EventsExchange`; `IntegrationJson.Options`.
- Produces:
  - `DomainEventContext(Guid MessageId, int Attempt, DateTimeOffset OccurredAt = default)`
  - `interface IIntegrationEventPublisher { Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken) where TEvent : IIntegrationEvent; }`
  - `IOrderRepository.GetForPublishingAsync(Guid id, CancellationToken)` → `Task<Order?>` (untracked, cross-owner)
  - `IntegrationEventRegistration.Outbound` (list of `IntegrationEventDescriptor(Type, string TypeName, string RoutingKey, Action<WolverineOptions> Route)`),
    `IntegrationEventRegistration.MapOutbound(WolverineOptions)`

- [ ] **Step 1: Write the failing Application tests**

`tests/Application.Tests/IntegrationEvents/IntegrationPublisherTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.IntegrationEvents;
using AiFramework.Application.Orders;
using AiFramework.Application.Tests.Orders;
using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.IntegrationEvents;

public sealed class IntegrationPublisherTests
{
    private static readonly Guid MessageId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DomainEventContext Context = new(MessageId, 1, OccurredAt);

    private readonly IIntegrationEventPublisher _publisher = Substitute.For<IIntegrationEventPublisher>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();

    [Fact]
    public async Task OrderPlaced_PublishesTheSnapshotWithTheOutboxMessageIdAsEventId()
    {
        var buyer = Guid.NewGuid();
        var order = Order.Place(Guid.NewGuid(), buyer, 2, OccurredAt, AnOrderedProduct.Any(), "SKU-1");
        _orders.GetForPublishingAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        await new OrderPlacedIntegrationPublisher(_orders, _publisher).HandleAsync(
            new OrderPlaced(order.Id, "SKU-1", 2), Context, CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            new OrderPlacedV1(MessageId, OccurredAt, order.Id, buyer, "SKU-1", 2,
                order.Product!.Name, order.Product.UnitPrice), // non-null: AnOrderedProduct.Any() supplies one
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPlaced_ForAnOrderThatNoLongerExists_PublishesNothing()
    {
        _orders.GetForPublishingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);

        await new OrderPlacedIntegrationPublisher(_orders, _publisher).HandleAsync(
            new OrderPlaced(Guid.NewGuid(), "SKU", 1), Context, CancellationToken.None);

        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync<OrderPlacedV1>(default!, default);
    }

    [Fact]
    public async Task OrderShipped_PublishesOrderShippedV1()
    {
        var orderId = Guid.NewGuid();
        var buyer = Guid.NewGuid();

        await new OrderShippedIntegrationPublisher(_publisher).HandleAsync(
            new OrderShipped(orderId, buyer, "SKU-1"), Context, CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            new OrderShippedV1(MessageId, OccurredAt, orderId, buyer, "SKU-1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderCancelled_PublishesTheReason()
    {
        var orderId = Guid.NewGuid();
        var buyer = Guid.NewGuid();

        await new OrderCancelledIntegrationPublisher(_publisher).HandleAsync(
            new OrderCancelled(orderId, buyer, "SKU-1", "Out of stock."), Context, CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            new OrderCancelledV1(MessageId, OccurredAt, orderId, buyer, "SKU-1", "Out of stock."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProductPriceChanged_PublishesBothPrices()
    {
        var productId = Guid.NewGuid();

        await new ProductPriceChangedIntegrationPublisher(_publisher).HandleAsync(
            new ProductPriceChanged(productId, "SKU-1", "Widget", 10m, 12.5m), Context, CancellationToken.None);

        await _publisher.Received(1).PublishAsync(
            new ProductPriceChangedV1(MessageId, OccurredAt, productId, "SKU-1", "Widget", 10m, 12.5m),
            Arg.Any<CancellationToken>());
    }
}
```

(`AnOrderedProduct` is the existing Application.Tests helper in `AiFramework.Application.Tests.Orders`.
`Order.Place` takes a non-nullable `OrderedProduct`, so a snapshot-less order cannot be built
here — Review Focus 4 is pinned end to end in Step 6 instead.)

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~IntegrationPublisherTests"`
Expected: FAIL — types missing.

- [ ] **Step 2: Carry `OccurredAt` on `DomainEventContext`**

`src/Application/Abstractions/DomainEventHandling.cs`:

```csharp
/// <summary>
/// What a handler needs to be idempotent, and when the event happened. MessageId is stable across
/// every redelivery of the same event, so it is the dedupe key. OccurredAt is the outbox row's own
/// timestamp - when the aggregate changed, not when this delivery ran - and defaults so the many
/// existing test constructions need no change.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct DomainEventContext(Guid MessageId, int Attempt, DateTimeOffset OccurredAt = default);
```

In `OutboxWorkItemProcessor.cs:71`, pass the row's timestamp:
`new DomainEventContext(item.Id, item.Attempt, item.OccurredAt)`. If the work item type does not
carry `OccurredAt`, add it where the poller builds work items from `OutboxMessage.OccurredAt`
(grep `new OutboxWorkItem(`) — it is already selected by the claim query's `ORDER BY "OccurredAt"`.

- [ ] **Step 3: The port, the repository read, and the four publishers**

`src/Application/IntegrationEvents/IIntegrationEventPublisher.cs`:

```csharp
namespace AiFramework.Application.IntegrationEvents;

/// <summary>
/// Publishes an integration event to other systems. Infrastructure decides how - today a
/// durable Wolverine outbox to RabbitMQ (ADR 0026) - so this layer references neither.
/// </summary>
public interface IIntegrationEventPublisher
{
    public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent;
}
```

`src/Application/Orders/IOrderRepository.cs`, beside `GetForFulfilmentAsync`:

```csharp
    /// <summary>
    /// Any buyer's order, UNTRACKED, for publishing <c>order.placed.v1</c>: the event itself does
    /// not carry the buyer or the product snapshot. Cross-owner and named for its one purpose, for
    /// the reason <see cref="GetForFulfilmentAsync"/> gives. ADR 0026.
    /// </summary>
    public Task<Order?> GetForPublishingAsync(Guid id, CancellationToken cancellationToken);
```

`src/Infrastructure/Persistence/OrderRepository.cs`:

```csharp
    // Untracked: the publisher only reads. See the port.
    public Task<Order?> GetForPublishingAsync(Guid id, CancellationToken cancellationToken) =>
        context.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
```

`src/Application/IntegrationEvents/IntegrationPublishers.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using AiFramework.Domain.Products;

namespace AiFramework.Application.IntegrationEvents;

// One per domain event, each mapping to its versioned contract. Idempotent by construction: they
// write nothing of their own, and a redelivery republishes with the SAME EventId, which is what
// consumers deduplicate on. Delivery is at least once - see the messaging skill.

public sealed class OrderPlacedIntegrationPublisher(
    IOrderRepository orders, IIntegrationEventPublisher publisher) : IDomainEventHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // OrderPlaced predates the notification feed and carries no buyer; the order's snapshot
        // fields never change after placement, so reading them late is safe.
        var order = await orders.GetForPublishingAsync(domainEvent.OrderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return; // Nothing to describe. The order was placed in this same transaction, so this is not expected.
        }

        await publisher.PublishAsync(
            new OrderPlacedV1(
                context.MessageId, context.OccurredAt, order.Id, order.UserId, order.Sku, order.Quantity,
                order.Product?.Name, order.Product?.UnitPrice),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class OrderShippedIntegrationPublisher(IIntegrationEventPublisher publisher)
    : IDomainEventHandler<OrderShipped>
{
    public Task HandleAsync(OrderShipped domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return publisher.PublishAsync(
            new OrderShippedV1(context.MessageId, context.OccurredAt, domainEvent.OrderId, domainEvent.UserId, domainEvent.Sku),
            cancellationToken);
    }
}

public sealed class OrderCancelledIntegrationPublisher(IIntegrationEventPublisher publisher)
    : IDomainEventHandler<OrderCancelled>
{
    public Task HandleAsync(OrderCancelled domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return publisher.PublishAsync(
            new OrderCancelledV1(context.MessageId, context.OccurredAt, domainEvent.OrderId, domainEvent.UserId,
                domainEvent.Sku, domainEvent.Reason),
            cancellationToken);
    }
}

public sealed class ProductPriceChangedIntegrationPublisher(IIntegrationEventPublisher publisher)
    : IDomainEventHandler<ProductPriceChanged>
{
    public Task HandleAsync(ProductPriceChanged domainEvent, DomainEventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return publisher.PublishAsync(
            new ProductPriceChangedV1(context.MessageId, context.OccurredAt, domainEvent.ProductId, domainEvent.Sku,
                domainEvent.Name, domainEvent.OldPrice, domainEvent.NewPrice),
            cancellationToken);
    }
}
```

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~IntegrationPublisherTests"`
Expected: PASS (5 tests).

- [ ] **Step 4: Write the failing registration test**

`tests/Infrastructure.Tests/Integration/IntegrationEventRegistrationTests.cs`:

```csharp
using AiFramework.Application.IntegrationEvents;
using AiFramework.Infrastructure.Integration;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Integration;

/// <summary>
/// The completeness net for outbound contracts, like JobRegistrationTests is for jobs: a contract
/// with no route is published into nothing, silently.
/// </summary>
public sealed class IntegrationEventRegistrationTests
{
    [Fact]
    public void EveryIntegrationEvent_IsRegisteredForOutboundRouting()
    {
        var declared = typeof(IIntegrationEvent).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(IIntegrationEvent)));

        var registered = IntegrationEventRegistration.Outbound.Select(d => d.EventType);

        registered.Should().BeEquivalentTo(declared);
    }

    [Fact]
    public void EveryRegistration_HasAUniqueTypeName()
    {
        IntegrationEventRegistration.Outbound.Select(d => d.TypeName).Should().OnlyHaveUniqueItems();
    }
}
```

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~IntegrationEventRegistrationTests"`
Expected: FAIL — `IntegrationEventRegistration` missing.

- [ ] **Step 5: Registration, mapper and adapter (Infrastructure)**

`src/Infrastructure/Integration/IntegrationEventRegistration.cs`:

```csharp
using AiFramework.Application.IntegrationEvents;
using AiFramework.Infrastructure.EventPath;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.Runtime.Serialization;

namespace AiFramework.Infrastructure.Integration;

/// <summary>One outbound contract's registration, captured over the closed generic (reflection-free, like JobDescriptor).</summary>
public sealed record IntegrationEventDescriptor(
    Type EventType, string TypeName, string RoutingKey, Action<WolverineOptions> Route)
{
    public static IntegrationEventDescriptor For<TEvent>()
        where TEvent : IIntegrationEvent =>
        new(typeof(TEvent), TEvent.TypeName, TEvent.RoutingKey, static opts =>
            opts.PublishMessage<TEvent>()
                .ToRabbitRoutingKey(RabbitMqTopology.EventsExchange, TEvent.RoutingKey)
                .UseInterop(IntegrationEnvelopeMapper.Instance)
                .DefaultSerializer(new SystemTextJsonSerializer(IntegrationJson.Options)));
}

/// <summary>Every outbound contract, explicitly. IntegrationEventRegistrationTests enforces completeness.</summary>
public static class IntegrationEventRegistration
{
    public static IReadOnlyList<IntegrationEventDescriptor> Outbound { get; } =
    [
        IntegrationEventDescriptor.For<OrderPlacedV1>(),
        IntegrationEventDescriptor.For<OrderShippedV1>(),
        IntegrationEventDescriptor.For<OrderCancelledV1>(),
        IntegrationEventDescriptor.For<ProductPriceChangedV1>(),
    ];

    /// <summary>Type → versioned name, for the envelope mapper.</summary>
    internal static IReadOnlyDictionary<Type, string> TypeNames { get; } =
        Outbound.ToDictionary(static d => d.EventType, static d => d.TypeName);

    /// <summary>Routes every outbound contract. Runs on both hosts: both publish.</summary>
    public static void MapOutbound(WolverineOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        foreach (var descriptor in Outbound)
        {
            descriptor.Route(opts);
        }
    }
}
```

`src/Infrastructure/Integration/IntegrationEnvelopeMapper.cs` (signature per V2):

```csharp
using AiFramework.Application.IntegrationEvents;
using RabbitMQ.Client;
using Wolverine;
using Wolverine.RabbitMQ.Internal;

namespace AiFramework.Infrastructure.Integration;

/// <summary>
/// Writes the standard AMQP properties an external consumer reads - message_id, type, content_type,
/// correlation_id - instead of Wolverine's own headers. Plain JSON on the wire, per ADR 0026.
/// </summary>
public sealed class IntegrationEnvelopeMapper : IRabbitMqEnvelopeMapper
{
    public static IntegrationEnvelopeMapper Instance { get; } = new();

    public void MapEnvelopeToOutgoing(Envelope envelope, IBasicProperties outgoing)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(outgoing);

        // EventId, not the envelope id: the envelope id changes if the domain event is redelivered
        // and published again; EventId does not, and it is what consumers dedupe on.
        if (envelope.Message is IIntegrationEvent integrationEvent)
        {
            outgoing.MessageId = integrationEvent.EventId.ToString();
            outgoing.Type = IntegrationEventRegistration.TypeNames[envelope.Message.GetType()];
        }

        outgoing.ContentType = "application/json";
        outgoing.Persistent = true;
        outgoing.CorrelationId = envelope.CorrelationId;
    }

    // Outbound only - nothing listens through this mapper.
    public void MapIncomingToEnvelope(Envelope envelope, IReadOnlyBasicProperties incoming)
    {
    }
}
```

(S108 forbids an empty block without a comment; the comment above satisfies it. If the analyzer
still objects, add `ArgumentNullException.ThrowIfNull(envelope);` inside.)

`src/Infrastructure/Integration/WolverineIntegrationEventPublisher.cs`:

```csharp
using AiFramework.Application.IntegrationEvents;
using Wolverine;

namespace AiFramework.Infrastructure.Integration;

/// <summary>
/// The port over Wolverine. PublishAsync writes an envelope to the durable outbox (Postgres); the
/// sending agent delivers it to RabbitMQ and retries through outages. ADR 0026.
/// </summary>
public sealed class WolverineIntegrationEventPublisher(IMessageBus bus) : IIntegrationEventPublisher
{
    public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent =>
        // Checked, not forwarded: PublishAsync takes no token (JobScheduler records the same).
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : bus.PublishAsync(integrationEvent).AsTask();
}
```

In `WolverineEventPath.ConfigureTransport`, after `ConfigureRabbitMq(...)`:

```csharp
        // Both hosts publish integration events: the outbox pump runs in both.
        IntegrationEventRegistration.MapOutbound(opts);
```

In `InfrastructureRegistration.cs`, beside the notifier registrations:

```csharp
        // Integration events to other systems (ADR 0026) - a fan-out beside the notifiers, so a
        // broker outage never disturbs them: publishing only writes an envelope row.
        services.AddScoped<IDomainEventHandler<OrderPlaced>, OrderPlacedIntegrationPublisher>();
        services.AddScoped<IDomainEventHandler<OrderShipped>, OrderShippedIntegrationPublisher>();
        services.AddScoped<IDomainEventHandler<OrderCancelled>, OrderCancelledIntegrationPublisher>();
        services.AddScoped<IDomainEventHandler<ProductPriceChanged>, ProductPriceChangedIntegrationPublisher>();
        services.AddScoped<IIntegrationEventPublisher, WolverineIntegrationEventPublisher>();
```

Run: `dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~Integration"`
Expected: PASS.

- [ ] **Step 6: Write the failing end-to-end outbound tests**

`tests/Api.IntegrationTests/Messaging/OutboundEventTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiFramework.Api.IntegrationTests.Orders;
using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.Api.IntegrationTests.Messaging;

/// <summary>Placing an order reaches RabbitMQ as order.placed.v1. ADR 0026.</summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class OutboundEventTests(ApiFactory factory)
{
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(30);

    private async Task<Guid> PlaceOrderAsync()
    {
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(factory);
        var response = await client.PostAsJsonAsync("/api/orders", new { Sku = sku, Quantity = 2 });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<Guid>();
    }

    [Fact]
    public async Task PlacingAnOrder_PublishesOrderPlacedV1WithItsProperties()
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var queue = await probe.BindTemporaryQueueAsync(RabbitMqTopology.EventsExchange, "order.placed");
        var orderId = await PlaceOrderAsync();

        await factory.DrainOutboxUntilEmptyAsync();
        var message = await FindAsync(probe, queue, orderId);

        message.Should().NotBeNull("the durable outbox must deliver the event to a bound queue");
        message!.Value.Type.Should().Be("order.placed.v1");
        message.Value.ContentType.Should().Be("application/json");
        Guid.TryParse(message.Value.MessageId, out _).Should().BeTrue("message_id is the eventId");
        message.Value.Body.GetProperty("quantity").GetInt32().Should().Be(2);
    }

    // Review Focus 4: an order from before the catalogue link has null product columns.
    [Fact]
    public async Task APlacedEventForALegacyOrder_CarriesNullProductFields()
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var queue = await probe.BindTemporaryQueueAsync(RabbitMqTopology.EventsExchange, "order.placed");
        var orderId = await PlaceOrderAsync();

        // Before draining: the publisher reads the order when the event is delivered, so this makes
        // it read exactly what a pre-catalogue row looks like. Columns per OrderConfiguration.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""update orders set "ProductId" = null, "ProductName" = null, "UnitPrice" = null where "Id" = {orderId}""");
        }

        await factory.DrainOutboxUntilEmptyAsync();
        var message = await FindAsync(probe, queue, orderId);

        message.Should().NotBeNull();
        message!.Value.Body.GetProperty("productName").ValueKind.Should().Be(JsonValueKind.Null);
        message.Value.Body.GetProperty("unitPrice").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AnEventNobodyBinds_IsKeptInTheUnroutedQueue()
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var before = await probe.MessageCountAsync(RabbitMqTopology.UnroutedQueue);

        // product.price_changed has no binding in this test run: a price update is the event.
        using var admin = await factory.CreateAdminClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(factory, price: 10m);
        var products = await admin.GetFromJsonAsync<JsonElement>("/api/products?limit=100");
        var id = products.GetProperty("items").EnumerateArray()
            .Single(p => p.GetProperty("sku").GetString() == sku).GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"/api/products/{id}", new { Name = "Widget", Description = (string?)null, Price = 12m }))
            .IsSuccessStatusCode.Should().BeTrue();

        await factory.DrainOutboxUntilEmptyAsync();

        // Poll the count through the same bounded wait: delivery is the broker's, not ours.
        var deadline = DateTime.UtcNow + Delivery;
        var after = before;
        while (after <= before && DateTime.UtcNow < deadline)
        {
            after = await probe.MessageCountAsync(RabbitMqTopology.UnroutedQueue);
            if (after <= before) await Task.Delay(100);
        }

        after.Should().BeGreaterThan(before,
            "an event with no bound consumer must be kept by the alternate exchange, not dropped");
    }

    private static async Task<(string? Type, string? ContentType, string? MessageId, JsonElement Body)?> FindAsync(
        BrokerProbe probe, string queue, Guid orderId)
    {
        var deadline = DateTime.UtcNow + Delivery;
        while (DateTime.UtcNow < deadline)
        {
            var got = await probe.WaitForMessageAsync(queue, deadline - DateTime.UtcNow);
            if (got is null) return null;
            var body = JsonDocument.Parse(Encoding.UTF8.GetString(got.Body.Span)).RootElement.Clone();
            if (body.GetProperty("orderId").GetGuid() == orderId)
            {
                return (got.BasicProperties.Type, got.BasicProperties.ContentType, got.BasicProperties.MessageId, body);
            }
        }

        return null;
    }
}
```

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~OutboundEventTests"`
Expected (before Step 5 is in): FAIL. After Step 5: continue to Step 7.

- [ ] **Step 7: Regenerate both trees, run, commit**

Regenerate exactly as Task 4 Step 4 (new routes can add router types to `GeneratedHandlerRegistry`).

Run: `dotnet test --nologo -v q`
Expected: all pass, including `OutboundEventTests`.

```bash
git add src/ tests/
git commit -m "feat(messaging): publish order and product events to RabbitMQ"
```

---

## Task 7: Broker outages

**Files:**
- Create: `tests/Api.IntegrationTests/Messaging/BrokerOutageTests.cs`
- Modify: `tests/Api.IntegrationTests/ApiFactory.cs`, `tests/Worker.IntegrationTests/WorkerFactory.cs` (pause/unpause)
- Create: tests in `tests/Worker.IntegrationTests/Messaging/ShipmentInboundTests.cs` are Task 8; the
  listener reconnect test lives there (Step 5 below adds it after Task 8's file exists — do Task 8
  first if executing out of order).

**Interfaces:**
- Produces: `ApiFactory.PauseBrokerAsync()`, `ApiFactory.UnpauseBrokerAsync()` (and the same on
  `WorkerFactory`).

- [ ] **Step 1: Pause/unpause on the factories**

In `ApiFactory` (and `WorkerFactory`), using the V4 finding (expected: Testcontainers 4.15
`PauseAsync`/`UnpauseAsync`, which keep the mapped port — a stop/start would move it and strand
Wolverine's connection):

```csharp
    /// <summary>Simulates a broker outage without changing its address. BrokerOutageTests only.</summary>
    public Task PauseBrokerAsync() => _rabbit.PauseAsync();

    public Task UnpauseBrokerAsync() => _rabbit.UnpauseAsync();
```

- [ ] **Step 2: Write the outage test**

`tests/Api.IntegrationTests/Messaging/BrokerOutageTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using AiFramework.Api.IntegrationTests.Orders;
using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AiFramework.Api.IntegrationTests.Messaging;

/// <summary>
/// The reason for the durable outbox (spec section 3, approach A): a broker outage delays an event,
/// it never loses one, and it never disturbs the request that caused it.
/// </summary>
/// <remarks>
/// Runs inside ApiFactoryCollection, whose tests run one at a time, so pausing the shared broker
/// cannot break a neighbour mid-flight. It always unpauses in a finally.
/// </remarks>
[Collection(nameof(ApiFactoryCollection))]
public sealed class BrokerOutageTests(ApiFactory factory)
{
    [Fact]
    public async Task AnEventPublishedDuringAnOutage_IsDeliveredAfterIt()
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var queue = await probe.BindTemporaryQueueAsync(RabbitMqTopology.EventsExchange, "order.placed");
        using var client = await factory.CreateAuthenticatedClientAsync();
        var sku = await CatalogueSetup.CreateProductAsync(factory);

        await factory.PauseBrokerAsync();
        try
        {
            var placed = await client.PostAsJsonAsync("/api/orders", new { Sku = sku, Quantity = 1 });

            placed.StatusCode.Should().Be(HttpStatusCode.Created, "a broker outage must not fail a request");
            await factory.DrainOutboxUntilEmptyAsync(); // the fan-out completes: publishing only wrote a row
            (await PendingOutgoingAsync()).Should().BeGreaterThan(0, "the event waits in Postgres");
        }
        finally
        {
            await factory.UnpauseBrokerAsync();
        }

        // Wolverine's sending agent retries on its own schedule after its circuit breaker, which is
        // why this wait is longer than the others.
        var delivered = await probe.WaitForMessageAsync(queue, TimeSpan.FromSeconds(90));
        delivered.Should().NotBeNull("the durable outbox must drain once the broker is back");
    }

    private async Task<long> PendingOutgoingAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();
        // Table name per V4.
        await using var command = new NpgsqlCommand(
            "select count(*) from wolverine.wolverine_outgoing_envelopes", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
```

- [ ] **Step 3: Run it**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~BrokerOutageTests"`
Expected: PASS. If delivery exceeds 90s, record the observed recovery time from V4 and set the
wait to twice that — do not shorten Wolverine's backoff in production code to make a test pass.

- [ ] **Step 4: Run the whole Api suite to prove the pause is contained**

Run: `dotnet test tests/Api.IntegrationTests --nologo`
Expected: all pass.

- [ ] **Step 5: The listener reconnect test (Review Focus 5) — add after Task 8**

In `tests/Worker.IntegrationTests/Messaging/ShipmentInboundTests.cs` (Task 8), add:

```csharp
    [Fact]
    public async Task AfterABrokerOutage_TheWorkerStillConsumesShipments()
    {
        var orderId = await PlaceOrderAsync();
        await factory.PauseBrokerAsync();
        await factory.UnpauseBrokerAsync();

        await PublishShipmentAsync(orderId, "WH-RECONNECT");

        (await WaitForStatusAsync(orderId, OrderStatus.Shipped, TimeSpan.FromSeconds(90)))
            .Should().BeTrue("the listener must reconnect on its own after the broker returns");
    }
```

Run: `dotnet test tests/Worker.IntegrationTests --filter "FullyQualifiedName~AfterABrokerOutage"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add tests/
git commit -m "test(messaging): a broker outage delays events and never loses them"
```

---

## Task 8: Inbound shipments

**Files:**
- Create: `src/Application/Orders/RecordShipment.cs`
- Create: `tests/Application.Tests/Orders/RecordShipmentHandlerTests.cs`
- Create: `src/Infrastructure/Integration/ShipmentConfirmedHandler.cs`
- Modify: `src/Infrastructure/Integration/IntegrationEventRegistration.cs` (inbound listener)
- Modify: `src/Infrastructure/EventPath/WolverineEventPath.cs` (discovery + listener, worker only)
- Modify: `src/Infrastructure/InfrastructureRegistration.cs` (command + validator)
- Create: `tests/Worker.IntegrationTests/Messaging/ShipmentInboundTests.cs`
- Create: `tests/Worker.IntegrationTests/AnOrderedProduct.cs` — a copy of the Api.IntegrationTests
  helper (`public static OrderedProduct Any() => new(Guid.NewGuid(), "Widget", 9.99m);`) in this
  project's namespace; `Order.Place` requires a product
- Modify: `src/Worker/Internal/Generated/**` (regenerated)

**Interfaces:**
- Consumes: `ShipmentConfirmedV1`, `IntegrationJson.Options`, `RabbitMqTopology.ShipmentsQueue`,
  `IOrderRepository.GetForFulfilmentAsync`, `IDeadLetterStore.ReplayAsync`.
- Produces: `RecordShipment(Guid OrderId, string ShipmentId, DateTimeOffset ShippedAt) : ICommand<ShipmentOutcome>`;
  `enum ShipmentOutcome { Shipped, AlreadyShipped }`; `IntegrationMessageRejectedException`;
  `IntegrationEventRegistration.ListenForShipments(WolverineOptions)`.

- [ ] **Step 1: Write the failing command tests**

`tests/Application.Tests/Orders/RecordShipmentHandlerTests.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.Orders;
using AiFramework.Domain.Orders;
using FluentAssertions;
using FluentValidation.TestHelper;
using NSubstitute;

namespace AiFramework.Application.Tests.Orders;

public sealed class RecordShipmentHandlerTests
{
    private static readonly DateTimeOffset PlacedAt = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public RecordShipmentHandlerTests() => _clock.UtcNow.Returns(Now);

    private Order Stored()
    {
        var order = Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1, PlacedAt, AnOrderedProduct.Any(), "SKU-1");
        _orders.GetForFulfilmentAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        return order;
    }

    private RecordShipmentHandler Handler() => new(_orders);

    [Fact]
    public async Task HandleAsync_OnAPlacedOrder_ShipsItAtTheWarehousesTime()
    {
        var order = Stored();
        var shippedAt = Now.AddMinutes(-5);

        var result = await Handler().HandleAsync(new RecordShipment(order.Id, "WH-1", shippedAt), CancellationToken.None);

        result.Value.Should().Be(ShipmentOutcome.Shipped);
        order.Status.Should().Be(OrderStatus.Shipped);
        order.ShippedAt.Should().Be(shippedAt);
    }

    [Fact]
    public async Task HandleAsync_OnAnAlreadyShippedOrder_IsANoOpSuccess()
    {
        var order = Stored();
        order.Ship(Now.AddMinutes(-30));

        var result = await Handler().HandleAsync(new RecordShipment(order.Id, "WH-2", Now), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a redelivered or duplicate confirmation must not fail");
        result.Value.Should().Be(ShipmentOutcome.AlreadyShipped);
        order.ShippedAt.Should().Be(Now.AddMinutes(-30), "the first shipment's time stands");
    }

    [Fact]
    public async Task HandleAsync_OnACancelledOrder_IsAConflict()
    {
        var order = Stored();
        order.Cancel("Out of stock.", Now.AddMinutes(-30));

        var result = await Handler().HandleAsync(new RecordShipment(order.Id, "WH-1", Now), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Conflict);
    }

    [Fact]
    public async Task HandleAsync_ForAnUnknownOrder_IsNotFound()
    {
        _orders.GetForFulfilmentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Order?)null);

        var result = await Handler().HandleAsync(new RecordShipment(Guid.NewGuid(), "WH-1", Now), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    // Review Focus 3.
    [Fact]
    public async Task HandleAsync_ShippedBeforeTheOrderWasPlaced_IsRejected()
    {
        var order = Stored();

        var result = await Handler().HandleAsync(
            new RecordShipment(order.Id, "WH-1", PlacedAt.AddHours(-1)), CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Validation);
        order.Status.Should().Be(OrderStatus.Placed);
    }

    [Fact]
    public void Validator_WithAShipmentTimeFarInTheFuture_Fails()
    {
        var result = new RecordShipmentValidator(_clock).TestValidate(
            new RecordShipment(Guid.NewGuid(), "WH-1", Now.AddMinutes(6)));

        result.ShouldHaveValidationErrorFor(c => c.ShippedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Validator_WithABlankShipmentId_Fails(string shipmentId)
    {
        var result = new RecordShipmentValidator(_clock).TestValidate(
            new RecordShipment(Guid.NewGuid(), shipmentId, Now));

        result.ShouldHaveValidationErrorFor(c => c.ShipmentId);
    }

    [Fact]
    public void Validator_WithAnEmptyOrderId_Fails()
    {
        var result = new RecordShipmentValidator(_clock).TestValidate(
            new RecordShipment(Guid.Empty, "WH-1", Now));

        result.ShouldHaveValidationErrorFor(c => c.OrderId);
    }
}
```

(If Application.Tests does not already reference `FluentValidation.TestHelper`, it ships inside
the `FluentValidation` package — check an existing validator test for the pattern used and match
it instead.)

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~RecordShipment"`
Expected: FAIL — types missing.

- [ ] **Step 2: The command**

`src/Application/Orders/RecordShipment.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Domain;
using AiFramework.Domain.Orders;
using FluentValidation;

namespace AiFramework.Application.Orders;

/// <summary>
/// A warehouse reports an order shipped (<c>shipment.confirmed.v1</c>). The worker's counterpart
/// of the operator's <see cref="ShipOrder"/>, and deliberately a different command: there is no
/// signed-in caller, the time is the warehouse's, and a redelivery must succeed. ADR 0026.
/// </summary>
/// <remarks>
/// Not <c>IInvalidatesCache</c>: the worker runs with the cache off, and the buyer's own cached
/// reads lag by their 30-second TTL - the limit ADR 0024 already accepted for an operator's ship.
/// </remarks>
public sealed record RecordShipment(Guid OrderId, string ShipmentId, DateTimeOffset ShippedAt)
    : ICommand<ShipmentOutcome>;

public enum ShipmentOutcome
{
    Shipped,
    AlreadyShipped,
}

public sealed class RecordShipmentValidator : AbstractValidator<RecordShipment>
{
    /// <summary>Clock skew we tolerate from a warehouse's clock.</summary>
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);

    public RecordShipmentValidator(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.ShipmentId).NotEmpty().MaximumLength(128);
        RuleFor(c => c.ShippedAt)
            .Must(shippedAt => shippedAt <= clock.UtcNow + MaximumFutureSkew)
            .WithMessage("A shipment cannot be dated more than five minutes in the future.");
    }
}

public sealed class RecordShipmentHandler(IOrderRepository orders)
    : ICommandHandler<RecordShipment, ShipmentOutcome>
{
    public async Task<Result<ShipmentOutcome>> HandleAsync(
        RecordShipment command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var order = await orders
            .GetForFulfilmentAsync(command.OrderId, cancellationToken)
            .ConfigureAwait(false);

        if (order is null)
        {
            return Result.Failure<ShipmentOutcome>(new Error(
                ErrorKind.NotFound, "orders.not_found", "That order does not exist."));
        }

        // Idempotent by state, not by message id: an external producer may send no message id at
        // all, and a redelivery or a second confirmation must not fail.
        if (order.Status is OrderStatus.Shipped)
        {
            return Result.Success(ShipmentOutcome.AlreadyShipped);
        }

        if (command.ShippedAt < order.PlacedAt)
        {
            return Result.Failure<ShipmentOutcome>(new Error(
                ErrorKind.Validation, "shipments.before_placed",
                "A shipment cannot be dated before the order was placed."));
        }

        // OrderStateException only, as ShipOrderHandler records: the narrow type keeps an input
        // rule from ever turning into a 409-shaped conflict.
        try
        {
            order.Ship(command.ShippedAt);
        }
        catch (OrderStateException exception)
        {
            return Result.Failure<ShipmentOutcome>(new Error(
                ErrorKind.Conflict, "orders.cannot_ship", exception.Message));
        }

        return Result.Success(ShipmentOutcome.Shipped);
    }
}
```

Register in `InfrastructureRegistration.cs` beside `ShipOrder`:

```csharp
        services.AddCommand<RecordShipment, ShipmentOutcome, RecordShipmentHandler>();
```

and beside `ShipOrderValidator`:

```csharp
        services.AddScoped<IValidator<RecordShipment>, RecordShipmentValidator>();
```

Run: `dotnet test tests/Application.Tests --filter "FullyQualifiedName~RecordShipment"` and
`dotnet test tests/Infrastructure.Tests --filter "FullyQualifiedName~RegistrationCompleteness"`
Expected: PASS both.

- [ ] **Step 3: Write the failing inbound tests**

`tests/Worker.IntegrationTests/Messaging/ShipmentInboundTests.cs`:

```csharp
using System.Text.Json;
using AiFramework.Application.IntegrationEvents;
using AiFramework.Application.Monitoring;
using AiFramework.Domain.Orders;
using AiFramework.Infrastructure.EventPath;
using AiFramework.Infrastructure.Integration;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AiFramework.Worker.IntegrationTests.Messaging;

/// <summary>
/// shipment.confirmed.v1 from an external producer - RabbitMQ.Client, no Wolverine headers -
/// through the worker to the database, and back out as order.shipped.v1. ADR 0026.
/// </summary>
[Collection(nameof(WorkerFactoryCollection))]
public sealed class ShipmentInboundTests(WorkerFactory factory)
{
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(30);

    /// <summary>An order placed straight through the repository: the worker has no HTTP API.</summary>
    private async Task<Guid> PlaceOrderAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        var order = Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(-10), AnOrderedProduct.Any(), "SKU-INBOUND");
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order.Id;
    }

    private async Task PublishShipmentAsync(Guid orderId, string shipmentId, DateTimeOffset? shippedAt = null)
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var json = JsonSerializer.Serialize(
            new ShipmentConfirmedV1(shipmentId, orderId, shippedAt ?? DateTimeOffset.UtcNow),
            IntegrationJson.Options);
        await probe.PublishToQueueAsync(RabbitMqTopology.ShipmentsQueue, json);
    }

    private async Task<OrderStatus?> StatusAsync(Guid orderId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        return await context.Orders.AsNoTracking().Where(o => o.Id == orderId)
            .Select(o => (OrderStatus?)o.Status).SingleOrDefaultAsync();
    }

    /// <summary>
    /// Bounded wait on the database, for the same reason BrokerProbe.WaitForMessageAsync gives:
    /// the message crosses the broker, another process, before the worker handles it.
    /// </summary>
    private async Task<bool> WaitForStatusAsync(Guid orderId, OrderStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await StatusAsync(orderId) == expected) return true;
            await Task.Delay(100);
        }

        return false;
    }

    private async Task<long> DeadLettersMentioningAsync(string text)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();
        // Table per V3; the body is stored as bytea, so match on its text.
        await using var command = new NpgsqlCommand(
            "select count(*) from wolverine.wolverine_dead_letters where convert_from(body, 'UTF8') like @text",
            connection);
        command.Parameters.AddWithValue("text", $"%{text}%");
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<bool> WaitForDeadLetterAsync(string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await DeadLettersMentioningAsync(text) > 0) return true;
            await Task.Delay(100);
        }

        return false;
    }

    [Fact]
    public async Task AShipment_ShipsTheOrder()
    {
        var orderId = await PlaceOrderAsync();

        await PublishShipmentAsync(orderId, "WH-SHIPS");

        (await WaitForStatusAsync(orderId, OrderStatus.Shipped, Delivery)).Should().BeTrue();
    }

    [Fact]
    public async Task AShipment_PublishesOrderShippedV1_TheRoundTrip()
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        var queue = await probe.BindTemporaryQueueAsync(RabbitMqTopology.EventsExchange, "order.shipped");
        var orderId = await PlaceOrderAsync();

        await PublishShipmentAsync(orderId, "WH-ROUNDTRIP");

        // The worker's own outbox pump runs the fan-out (WorkerFactory keeps its hosted services).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var found = false;
        while (!found && DateTime.UtcNow < deadline)
        {
            var got = await probe.WaitForMessageAsync(queue, deadline - DateTime.UtcNow);
            if (got is null) break;
            found = got.BasicProperties.Type == "order.shipped.v1"
                && System.Text.Encoding.UTF8.GetString(got.Body.Span).Contains(orderId.ToString(), StringComparison.Ordinal);
        }

        found.Should().BeTrue("shipping through the broker must publish order.shipped.v1 back out");
    }

    [Fact]
    public async Task ADuplicateShipment_IsANoOpAndDeadLettersNothing()
    {
        var orderId = await PlaceOrderAsync();
        await PublishShipmentAsync(orderId, "WH-DUP");
        (await WaitForStatusAsync(orderId, OrderStatus.Shipped, Delivery)).Should().BeTrue();

        await PublishShipmentAsync(orderId, "WH-DUP");
        await PublishShipmentAsync(orderId, "WH-DUP-2");

        // A marker message after the duplicates: once it is handled, the duplicates were too.
        var marker = await PlaceOrderAsync();
        await PublishShipmentAsync(marker, "WH-MARKER");
        (await WaitForStatusAsync(marker, OrderStatus.Shipped, Delivery)).Should().BeTrue();

        (await DeadLettersMentioningAsync(orderId.ToString())).Should().Be(0);
    }

    [Fact]
    public async Task AShipmentForAnUnknownOrder_DeadLettersOnce()
    {
        var unknown = Guid.NewGuid();

        await PublishShipmentAsync(unknown, "WH-UNKNOWN");

        (await WaitForDeadLetterAsync(unknown.ToString(), Delivery)).Should().BeTrue(
            "a rejection must land on the monitoring page's dead letters, not retry forever");
    }

    [Fact]
    public async Task AShipmentForACancelledOrder_DeadLetters()
    {
        var orderId = await PlaceOrderAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
            var order = await context.Orders.SingleAsync(o => o.Id == orderId);
            order.Cancel("Out of stock.", DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }

        await PublishShipmentAsync(orderId, "WH-CANCELLED");

        (await WaitForDeadLetterAsync(orderId.ToString(), Delivery)).Should().BeTrue();
        (await StatusAsync(orderId)).Should().Be(OrderStatus.Cancelled);
    }

    // V3: the monitoring page's Retry must still work when the transport is RabbitMQ.
    [Fact]
    public async Task ADeadLetteredShipment_IsRedeliveredByRetry()
    {
        var unknown = Guid.NewGuid();
        await PublishShipmentAsync(unknown, "WH-RETRY");
        (await WaitForDeadLetterAsync(unknown.ToString(), Delivery)).Should().BeTrue();

        await using var scope = factory.Services.CreateAsyncScope();
        var deadLetters = scope.ServiceProvider.GetRequiredService<IDeadLetterStore>();
        var page = await deadLetters.ListAsync(1, 100, CancellationToken.None);
        // DeadLetterView (Application/Monitoring/MonitoringViews.cs): Id, MessageType, ExceptionMessage.
        var entry = page.Items.Single(i => i.MessageType.Contains("ShipmentConfirmed", StringComparison.Ordinal)
            && i.ExceptionMessage.Contains(unknown.ToString(), StringComparison.Ordinal));

        (await deadLetters.ReplayAsync(entry.Id, CancellationToken.None)).Should().BeTrue();

        // Still unknown, so it dead-letters again: two rows, or one row with a higher attempt count
        // (record which in V3). Either proves the replay ran the handler again.
        (await WaitForDeadLetterAsync(unknown.ToString(), Delivery)).Should().BeTrue();
    }
}
```

The error messages carry the order id because `IntegrationMessageRejectedException` (Step 4)
includes it; that is what the dead-letter queries match on.

Run: `dotnet test tests/Worker.IntegrationTests --filter "FullyQualifiedName~ShipmentInboundTests"`
Expected: FAIL — nothing listens on `aiframework.shipments`.

- [ ] **Step 4: The handler and the listener**

`src/Infrastructure/Integration/ShipmentConfirmedHandler.cs`:

```csharp
using AiFramework.Application.Abstractions;
using AiFramework.Application.IntegrationEvents;
using AiFramework.Application.Orders;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace AiFramework.Infrastructure.Integration;

/// <summary>
/// An inbound message that can never succeed as sent: not found, cancelled, invalid. Its message
/// names the order so an operator can find it on the monitoring page's dead letters.
/// </summary>
public sealed class IntegrationMessageRejectedException(string message) : Exception(message);

/// <summary>
/// The worker's adapter for shipment.confirmed.v1: dispatches <see cref="RecordShipment"/> through
/// the same pipeline an HTTP request uses (validation, logging). Worker-only: the API listens to no
/// queue. ADR 0026.
/// </summary>
public sealed class ShipmentConfirmedHandler(ICommandDispatcher commands)
{
    /// <summary>
    /// Rejections go straight to the dead letters - retrying cannot change "not found". Anything
    /// else (the database unreachable) falls to the worker's global policy: ScheduleRetry 1/5/30
    /// minutes, then dead letters, the job lanes' shape.
    /// </summary>
    public static void Configure(HandlerChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        chain.OnException<IntegrationMessageRejectedException>().MoveToErrorQueue();
    }

    public async Task Handle(ShipmentConfirmedV1 message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var result = await commands.SendAsync(
            new RecordShipment(message.OrderId, message.ShipmentId, message.ShippedAt),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new IntegrationMessageRejectedException(
                $"shipment {message.ShipmentId} for order {message.OrderId} rejected: " +
                $"{result.Error.Code} - {result.Error.Message}");
        }
    }
}
```

`IntegrationEventRegistration`, add:

```csharp
    /// <summary>The inbound listener. Worker only - never called for the API.</summary>
    public static void ListenForShipments(WolverineOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        opts.ListenToRabbitQueue(RabbitMqTopology.ShipmentsQueue)
            // Plain JSON from a producer with no Wolverine: every message on this queue is this type.
            .DefaultIncomingMessage<ShipmentConfirmedV1>()
            .DefaultSerializer(new SystemTextJsonSerializer(IntegrationJson.Options));
    }
```

In `WolverineEventPath.AddWolverineEventPath`, inside the existing `if (role is WolverineHostRole.ProcessesJobs)`
discovery block (outside the durable branch, so `codegen write` sees it):

```csharp
                opts.Discovery.IncludeType<ShipmentConfirmedHandler>();
```

and in `ConfigureJobs`, after `ListenForJobs(...)` (worker only, durable only):

```csharp
        IntegrationEventRegistration.ListenForShipments(opts);
```

Update `WolverineHostRole.ProcessesJobs`'s summary: "Listens on the job lanes and the inbound
integration queue, and runs their handlers. The worker."

- [ ] **Step 5: Regenerate the worker tree**

As Task 4 Step 4. The worker tree gains the `ShipmentConfirmedV1` adapter; the API tree should not
change. Then:
Run: `dotnet test --nologo -v q --filter "FullyQualifiedName~Codegen"`
Expected: PASS.

- [ ] **Step 6: Run the inbound tests**

Run: `dotnet test tests/Worker.IntegrationTests --filter "FullyQualifiedName~ShipmentInboundTests"`
Expected: PASS (6 tests).

- [ ] **Step 7: Prove the API still listens to nothing**

Run: `dotnet test tests/Api.IntegrationTests --filter "FullyQualifiedName~ApiPublishesOnly|FullyQualifiedName~TopologyTests"`
Expected: PASS.

- [ ] **Step 8: Add the reconnect test (Task 7 Step 5) now that this file exists, and run it**

- [ ] **Step 9: Malformed body (Review Focus 2)**

Add to `ShipmentInboundTests`:

```csharp
    [Fact]
    public async Task AShipmentThatIsNotJson_DeadLettersAndTheQueueKeepsMoving()
    {
        await using var probe = await BrokerProbe.ConnectAsync(factory.RabbitMqConnectionString);
        await probe.PublishToQueueAsync(RabbitMqTopology.ShipmentsQueue, "this is not json {");

        var next = await PlaceOrderAsync();
        await PublishShipmentAsync(next, "WH-AFTER-GARBAGE");

        (await WaitForStatusAsync(next, OrderStatus.Shipped, Delivery)).Should().BeTrue(
            "a poison message must not block the queue behind it");
    }
```

Run: `dotnet test tests/Worker.IntegrationTests --filter "FullyQualifiedName~NotJson"`
Expected: PASS. If it hangs, Wolverine is retrying the deserialization failure: add
`chain.OnException<System.Text.Json.JsonException>().MoveToErrorQueue();` in `Configure` — or, if
the failure occurs before a chain exists, the listener-level
`opts.Policies.OnException<JsonException>().MoveToErrorQueue()` per V5 — and re-run.

- [ ] **Step 10: Full backend run, then commit**

Run: `dotnet test --nologo -v q`
Expected: all pass.

```bash
git add src/ tests/
git commit -m "feat(messaging): ship orders from shipment.confirmed.v1 messages"
```

---

## Task 9: Kubernetes

**Files:**
- Create: `k8s/base/rabbitmq.yaml`
- Modify: `k8s/base/kustomization.yaml`, `k8s/overlays/local/secret.yaml`, `deploy/deploy.ps1`
- Modify: `.claude/skills/kubernetes/SKILL.md`

**Interfaces:**
- Produces: in-cluster broker at `rabbitmq:5672`; `ConnectionStrings__RabbitMq` in `app-secrets`.

- [ ] **Step 1: The manifest**

`k8s/base/rabbitmq.yaml`:

```yaml
apiVersion: v1
kind: Service
metadata:
  name: rabbitmq
spec:
  clusterIP: None
  selector:
    app: rabbitmq
  ports:
    - name: amqp
      port: 5672
      targetPort: 5672
    - name: management
      port: 15672
      targetPort: 15672
---
apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: rabbitmq
spec:
  serviceName: rabbitmq
  replicas: 1
  selector:
    matchLabels:
      app: rabbitmq
  template:
    metadata:
      labels:
        app: rabbitmq
    spec:
      containers:
        - name: rabbitmq
          # Matches docker-compose.yml, so the cluster and the dev loop agree on the broker.
          image: rabbitmq:4.3-management-alpine
          ports:
            - containerPort: 5672
            - containerPort: 15672
          env:
            - name: RABBITMQ_DEFAULT_USER
              valueFrom:
                secretKeyRef:
                  name: app-secrets
                  key: RABBITMQ_DEFAULT_USER
            - name: RABBITMQ_DEFAULT_PASS
              valueFrom:
                secretKeyRef:
                  name: app-secrets
                  key: RABBITMQ_DEFAULT_PASS
          # AMQP accepting, not merely "node up" - both hosts refuse to start if they connect first.
          readinessProbe:
            exec:
              command: ['rabbitmq-diagnostics', '-q', 'check_port_connectivity']
            initialDelaySeconds: 10
            periodSeconds: 10
            timeoutSeconds: 10
          resources:
            requests:
              cpu: 100m
              memory: 256Mi
            # RabbitMQ derives its memory high-watermark from this cgroup limit and blocks
            # publishers at it; sized like Postgres.
            limits:
              memory: 512Mi
          volumeMounts:
            - name: rabbitmqdata
              mountPath: /var/lib/rabbitmq
  # The StatefulSet's stable hostname (rabbitmq-0) is what keeps the node name - and therefore
  # the data directory - the same across restarts.
  volumeClaimTemplates:
    - metadata:
        name: rabbitmqdata
      spec:
        accessModes: ['ReadWriteOnce']
        resources:
          requests:
            storage: 1Gi
---
# maxUnavailable, not minAvailable: on a one-replica StatefulSet minAvailable: 1 blocks every node
# drain forever, as the worker's budget in autoscaling.yaml already records.
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: rabbitmq
spec:
  maxUnavailable: 1
  selector:
    matchLabels:
      app: rabbitmq
```

- [ ] **Step 2: Wire it in**

`k8s/base/kustomization.yaml`: add `- rabbitmq.yaml` after `- redis.yaml`.

`k8s/overlays/local/secret.yaml` `stringData:` (covered by the file's existing throwaway header):

```yaml
  RABBITMQ_DEFAULT_USER: aiframework
  RABBITMQ_DEFAULT_PASS: aiframework
  ConnectionStrings__RabbitMq: 'amqp://aiframework:aiframework@rabbitmq:5672/'
```

`deploy/deploy.ps1`, immediately after the `'Waiting for postgres'` step:

```powershell
# 2b. The broker too: both hosts refuse to start without it (ADR 0026), so letting phase C roll out
#     first would only buy CrashLoopBackOff and its growing restart delays. Named, not labelled, for
#     the reason the postgres wait above gives.
Invoke-Step 'Waiting for rabbitmq' {
    kubectl --context $context -n $namespace rollout status statefulset/rabbitmq --timeout=180s
}
```

- [ ] **Step 3: Render and deploy**

Run: `kubectl kustomize k8s/overlays/local | grep -c "name: rabbitmq"`
Expected: at least 3 (Service, StatefulSet, PDB).

Run: `./deploy/deploy.ps1 -CreateCluster` (or `./deploy/deploy.ps1` if the cluster exists)
Expected: "Waiting for rabbitmq" succeeds before migrations; API and worker roll out without
restarts: `kubectl -n aiframework get pods` shows `RESTARTS 0` for api and worker.

- [ ] **Step 4: Verify the topology in the cluster**

```bash
kubectl -n aiframework port-forward svc/rabbitmq 15672 &
curl -s -u aiframework:aiframework http://localhost:15672/api/exchanges/%2F/aiframework.events | grep -o '"type":"topic"'
curl -s -u aiframework:aiframework http://localhost:15672/api/queues | grep -o '"name":"aiframework[^"]*"'
```

Expected: `"type":"topic"`; the four `aiframework.*` queues.

- [ ] **Step 5: Kind e2e**

Run: `./deploy/e2e-k8s.ps1`
Expected: the untagged suite passes (28 tests at the time of writing; the new shipment spec is
`@local-only`).

- [ ] **Step 6: Docs and commit**

`.claude/skills/kubernetes/SKILL.md`: a RabbitMQ paragraph — StatefulSet with PVC pinned to its
node like Postgres (same drain note), the phase-A wait, the management UI via
`kubectl port-forward svc/rabbitmq 15672` and deliberately not on the ingress, readiness of the
app excludes the broker.

```bash
git add k8s/ deploy/deploy.ps1 .claude/skills/kubernetes/SKILL.md
git commit -m "feat(messaging): run RabbitMQ in the kind cluster"
```

---

## Task 10: End-to-end shipment

**Files:**
- Create: `frontend/e2e/support/broker.ts`
- Create: `frontend/e2e/specs/orders/shipment-inbound.spec.ts`
- Modify: `frontend/e2e/CLAUDE.md`

**Interfaces:**
- Consumes: `E2E_RABBITMQ_UI_URL` (Task 2), the `api` fixture, `workerUser`.
- Produces: `publishShipment(orderId: string, shipmentId: string): Promise<void>`.

- [ ] **Step 1: The broker helper**

`frontend/e2e/support/broker.ts`:

```ts
import { request } from '@playwright/test';
import { E2E_RABBITMQ_UI_URL } from './env.ts';

/**
 * Publishes shipment.confirmed.v1 as a warehouse would, through the e2e broker's management HTTP
 * API - no AMQP client dependency in this workspace. Managed stack only: the kind cluster exposes
 * no broker port to the host, which is why the spec using this is @local-only.
 */
export async function publishShipment(orderId: string, shipmentId: string): Promise<void> {
  const context = await request.newContext({
    httpCredentials: { username: 'e2e', password: 'e2e' },
  });

  try {
    const response = await context.post(
      `${E2E_RABBITMQ_UI_URL}/api/exchanges/%2F/amq.default/publish`,
      {
        data: {
          properties: { content_type: 'application/json', delivery_mode: 2 },
          routing_key: 'aiframework.shipments',
          payload: JSON.stringify({ shipmentId, orderId, shippedAt: new Date().toISOString() }),
          payload_encoding: 'string',
        },
      },
    );

    const body = (await response.json()) as { routed?: boolean };
    if (!response.ok() || body.routed !== true) {
      throw new Error(
        `Publishing shipment ${shipmentId} failed with ${String(response.status())}: ${JSON.stringify(body)}`,
      );
    }
  } finally {
    await context.dispose();
  }
}
```

- [ ] **Step 2: The spec**

`frontend/e2e/specs/orders/shipment-inbound.spec.ts`:

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as notifications from '../../screens/notifications.ts';
import { publishShipment } from '../../support/broker.ts';
import { uniqueSku } from '../../support/identity.ts';

/**
 * A warehouse confirms a shipment through RabbitMQ, and the buyer sees it (ADR 0026): broker ->
 * worker -> database -> outbox -> notification feed.
 *
 * @local-only: it publishes to the broker from the host, and only the managed stack exposes one.
 */
test('tells the buyer when the warehouse confirms a shipment', { tag: '@local-only' }, async ({
  signedInPage,
  api,
  workerUser,
}) => {
  const sku = uniqueSku();

  await test.step('arrange: an order, confirmed shipped by the warehouse', async () => {
    const orderId = await api.placeOrder(workerUser, { sku, quantity: 1 });
    await publishShipment(orderId, `WH-${sku}`);
    await api.waitForNotification(workerUser, { kind: 'OrderShipped', text: sku });
  });

  await notifications.open(signedInPage);

  await expect(
    notifications.item(signedInPage, `Your order for ${sku} is on its way.`),
  ).toContainText('Order shipped');
});
```

- [ ] **Step 3: Run it**

Run: `npm run e2e --prefix frontend -- --grep "warehouse confirms"`
Expected: PASS. Then the full `npm run e2e --prefix frontend`: all pass (57 tests).

- [ ] **Step 4: Docs and commit**

`frontend/e2e/CLAUDE.md`: add `shipment-inbound.spec.ts` to the Tags paragraph (tagged: needs the
host-exposed broker), add "shipment confirmed via the broker" to the Orders coverage row, and
recount "A `kind` run therefore executes 28 of the 57 tests" with
`npx playwright test --list` and `--grep-invert @local-only`.

```bash
git add frontend/e2e
git commit -m "test(messaging): end-to-end shipment confirmed through the broker"
```

---

## Task 11: Documentation

**Files:**
- Create: `docs/adr/0026-rabbitmq-for-asynchronous-work.md`, `.claude/skills/messaging/SKILL.md`
- Modify: `docs/adr/0016-jobs-in-a-worker-host.md`, `CLAUDE.md`, `.claude/skills/regenerate/SKILL.md`,
  `src/Application/Monitoring/RetryDeadLetter.cs` (remarks)

- [ ] **Step 1: ADR 0026** — use the `/adr` command's template. Context: the owner wants all
  asynchronous work on a broker, including an external consumer (ADR 0016's trigger 4) and an
  inbound producer; no server exists yet. Decision: sections 1–7 of the spec, one paragraph each,
  and the "requests stay synchronous" boundary. Consequences, including the costs: a broker in every
  environment; the API and worker refuse to start without it; at-least-once delivery with `eventId`
  dedupe; unrouted cap; **the broker credential is the access control for inbound — a real
  deployment needs a producer-only user for the warehouse**; jobs waiting in the old
  `wolverine_queues` tables at upgrade time are not migrated (drain before deploying: see the
  messaging skill). Alternatives: the rows of the spec's Decisions table.
- [ ] **Step 2: ADR 0016 note** — after "The transport stays PostgreSQL" paragraph, add a
  `> **Superseded by [ADR 0026](0026-rabbitmq-for-asynchronous-work.md) (date).**` block: trigger 4
  now holds; jobs ride RabbitMQ; Postgres still holds the envelope storage.
- [ ] **Step 3: The `messaging` skill** — `.claude/skills/messaging/SKILL.md` with frontmatter
  `name: messaging`, description "Use when touching RabbitMQ, integration events, the shipments
  listener, or anything that publishes or consumes a broker message". Sections: the topology table;
  the contracts and versioning rule; approach A's guarantees (at least once, dedupe on `eventId`,
  outage behaviour, no cross-event ordering); inbound rejection vs retry and where dead letters go;
  "adding a new outbound event" checklist (contract record → descriptor in
  `IntegrationEventRegistration.Outbound` → publisher handler → register → regenerate both trees);
  **upgrading from Postgres job queues**: before the first deploy of this change, confirm
  `select count(*) from wolverine_queues.wolverine_queue_jobs_light` (and `_heavy`, and their
  `_scheduled`) is 0, or let the old worker drain them first.
- [ ] **Step 4: Root CLAUDE.md** — "Running locally": `./scripts/dev.ps1  # Postgres + RabbitMQ + API (5234) + job worker (5235) + Vite (5173)`;
  the Jobs rule: "Jobs ride RabbitMQ quorum queues; the API listens to no queue"; add
  `messaging` to the skills table.
- [ ] **Step 5: regenerate skill** — one sentence under the contract incantation: RabbitMQ is
  configured only when Wolverine is durable, so `Wolverine__Durable=false` covers it and no
  broker variable is needed for `codegen write` or the contract.
- [ ] **Step 6: `RetryDeadLetter` remarks** — replace "the thing to re-examine if the transport
  ever becomes RabbitMQ" with "Verified with RabbitMQ listeners (ShipmentInboundTests.
  ADeadLetteredShipment_IsRedeliveredByRetry): dead letters stay in Postgres, so replay is
  transport-independent."
- [ ] **Step 7: Commit**

```bash
git add docs/adr .claude/skills CLAUDE.md src/Application/Monitoring/RetryDeadLetter.cs
git commit -m "docs(messaging): ADR 0026 and the messaging skill"
```

---

## Task 12: Full verification and review

- [ ] **Step 1: `/verify`** — build and test Debug **and** Release
  (`dotnet test -c Release`), lint/build/test the frontend, both codegen diffs, the contract diff,
  `npm run e2e`, the hook suite. Expected: all green; report anything skipped as skipped.
- [ ] **Step 2: Local-run checklist, by hand**
  1. `scripts/stop-dev.ps1`, then `docker compose down -v` (clean state).
  2. `local-run/control-panel.bat` → option 2. Expected: "Starting the dev database and message
     broker" completes; API and worker windows start without a connection error; the banner prints
     the RabbitMQ UI line.
  3. Open `http://localhost:55673` (aiframework / aiframework). Expected: exchanges
     `aiframework.events` (topic, alternate exchange set) and `aiframework.events.unrouted`; queues
     `aiframework.jobs.light`, `aiframework.jobs.heavy`, `aiframework.shipments`,
     `aiframework.events.unrouted`, all type quorum.
  4. In the app, place an order. Expected: `aiframework.events.unrouted` gains a message; "Get
     messages" shows JSON with `"type":"order.placed.v1"` in properties.
  5. Management UI → Queues → `aiframework.shipments` → Publish message: payload
     `{"shipmentId":"WH-MANUAL","orderId":"<that order id>","shippedAt":"<now ISO>"}`,
     property `content_type=application/json`. Expected: the order shows shipped and the bell
     notifies "Order shipped".
  6. Option 4 (worker only) while the loop runs: the worker restarts and reconnects.
  7. Option 5. Expected: the broker container stops; `docker volume ls` still lists `rabbitmqdata`.
  8. Option 3 (dev loop + Seq): place an order, then publish a manual shipment as in step 5, and
     open Seq. The `order.placed.v1` send carries the placing request's trace id (spec section 8).
     A shipment from an external producer starts a fresh trace, which is expected, since it sends no
     traceparent. Record what is seen in ADR 0026.
- [ ] **Step 3: Kind** — option 6 then option 9 (Task 9 Steps 3–5), on a cluster created from
  scratch so the "fresh broker" path is the one exercised.
- [ ] **Step 4: Reviewers** — `dotnet-reviewer` on the backend diff; `react-reviewer` on
  `frontend/`; fix what they find, re-run the affected tests.
- [ ] **Step 5: PR** — title `feat(messaging)!: RabbitMQ as the broker for all asynchronous work`;
  body lists the breaking change (both hosts require `ConnectionStrings__RabbitMq` and a reachable
  broker), the upgrade drain step, and what was verified where. **Check with the owner before
  opening it.** After merge, delete the local `feat/rabbitmq-transport` and
  `docs/rabbitmq-transport-spec` branches.
