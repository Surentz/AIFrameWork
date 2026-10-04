# Tests

One project per layer, mirroring `src/`.

| Project | Tests | Style |
|---|---|---|
| `Domain.Tests` | Entities, value objects, invariants | Pure. No mocks, no I/O. |
| `Application.Tests` | Use-case handlers | Ports substituted with NSubstitute |
| `Infrastructure.Tests` | Repositories, EF mapping | Real database via Testcontainers |
| `Api.IntegrationTests` | HTTP contract | `WebApplicationFactory<Program>` |
| `Worker.IntegrationTests` | Job delivery, lanes, scoping | The real worker host + Testcontainers |

## Stack

xUnit + FluentAssertions + NSubstitute.

## Registration completeness

`Infrastructure.Tests/Messaging/RegistrationCompletenessTests.cs` is what replaces
compile-time safety for `AddCommand`/`AddQuery`/validator registration: `AddCommand` and
`AddQuery` are reflection-free by design (see `src/Infrastructure/CLAUDE.md`), so a missing
registration compiles fine and only fails at dispatch time. These tests reflect over the
`Application` assembly instead, at test time, and fail the build if any `ICommand<>`,
`IQuery<>`, or `AbstractValidator<T>` lacks its registration. Deleting them removes the only
net catching that class of mistake.

## Architecture tests

Every test project has an `ArchitectureTests` class enforcing the root CLAUDE.md dependency
table. They read compiled assemblies, never a host, so they join no collection and need no Docker.

- **Domain, Application, Infrastructure** check assembly references by reflection.
- **Api and Worker** add "DI only" with ArchUnitNET, which reads IL: only the types in
  `CompositionTypes` (and the generated adapters under `Internal.Generated.`) may depend on an
  Infrastructure type. A new controller, hub or service that needs Infrastructure is the
  violation; a new composition file is added to that list deliberately, with a reason.
- **Each has a `Scan_*` guard** proving the scan returns something real, so a broken loader
  cannot turn the rule into a vacuous pass. Keep the guard when editing the rule.
- Assert violations as one joined string, not a collection: FluentAssertions' `BeEmpty()` on a
  collection prints only the first item.

## Testcontainers

`Infrastructure.Tests` shares one Postgres container across its whole test run via
`PostgresFixture` and `[Collection(nameof(PostgresCollection))]` — every test class that needs
the database joins that one collection, so xUnit starts exactly one container for all of them.
**One container per collection, never one per test class.** A new test class that needs
Postgres joins `PostgresCollection`; it does not declare its own fixture.

`Api.IntegrationTests` now follows this too: every class that needs an `ApiFactory` joins
`[Collection(nameof(ApiFactoryCollection))]` (the collection definition lives at the bottom of
`ApiFactory.cs`, mirroring `PostgresFixture.cs`'s pattern), so xUnit starts exactly one Postgres
container for the whole project. A new `Api.IntegrationTests` class that needs the database
joins that same collection — it does not declare its own `IClassFixture<ApiFactory>`.
`HealthTests` deliberately stays outside the collection: it uses its own
`IClassFixture<WebApplicationFactory<Program>>` because `/health` needs a host but never
touches the database, so it does not need the shared container.

**Both integration factories also start a RabbitMQ container** (`rabbitmq:4.3-management-alpine`,
`Testcontainers.RabbitMq`), beside Postgres and on the same terms: one per collection, started
with it in `InitializeAsync`. Durable Wolverine connects to the broker while the host starts (ADR
0026), so the host cannot be built without one. `Infrastructure.Tests` needs none.

- **`Messaging/BrokerProbe`** (one per project) is how a test reads or writes the broker directly,
  the way an external system would: RabbitMQ.Client, no Wolverine, server-named exclusive queues,
  so tests in one collection never see each other's messages. Connect it with the factory's
  `RabbitMqConnectionString`.
- **Simulate a broker outage with `StopBrokerAppAsync()`/`StartBrokerAppAsync()`**
  (`rabbitmqctl stop_app`/`start_app` inside the container), **never** Testcontainers'
  `PauseAsync`. A paused container is a TCP black hole — a network partition, not an outage — and
  `IMessageBus.PublishAsync` blocks on it until the broker returns, so the test hangs inside the
  outbox drain. `stop_app` closes every connection but keeps the container and its mapped port,
  which is a real outage: publishes return at once and drain within seconds of `start_app`.
  Allow up to 60 s for that delivery; it is a real-time wait on the broker, not a poll interval.
- Docker Desktop occasionally fails a container start with
  `System.IO.IOException : Invalid chunk header encountered`. That is the Docker API, not the
  test: re-run once before treating it as real. Two containers per collection make it twice as
  likely to show up.

## Outbox tests

Two projects cover the outbox, joining different collections for a reason:

- `Infrastructure.Tests/Outbox/*` (`OutboxAtomicityTests`, `OutboxPollerTests`,
  `OutboxWorkItemProcessorTests`, `OutboxRegistrationTests`, `DomainEventRegistryTests`) joins
  `PostgresCollection` like every other database test in that project.
- `Api.IntegrationTests/Orders/OutboxDeliveryTests.cs` joins `ApiFactoryCollection` instead — it
  needs the host (to place an order over HTTP), not just the database.
- `Worker.IntegrationTests/OutboxPumpTests.cs` pins that the worker runs **no** pump (ADR 0028),
  and `OutboxRegistrationTests.AddOutbox_Alone_StartsNoPump` that Infrastructure alone starts
  none. `ApiFactory` throws at build time if the Api lost its `AddOutboxPumps()` call.

Timing throughout is driven by an injected `IClock`/`TestClock`, never by waiting on a poll
interval — see the "no `Thread.Sleep`" rule below.

`OutboxDeliveryTests` drives delivery by hand through `ApiFactory.DrainOutboxUntilEmptyAsync`,
which claims and processes batches until a claim comes back empty (bounded by an internal
`MaxBatches` safety cap), rather than waiting on the hosted pumps' own timing. It has to loop,
not stop after one claim: `OutboxPoller.ClaimAsync` is `ORDER BY "OccurredAt" LIMIT BatchSize`,
and every `Api.IntegrationTests` class now shares one database via `ApiFactoryCollection`, so a
single claim could leave the row a later test cares about sitting unclaimed behind rows an
earlier test left in the queue. The contract this proves is: everything currently due drains,
deterministically, with no waiting — not "one poll cycle runs." It does not exercise the channel
hop (`ChannelWriter`/`ChannelReader`), backpressure, or `WorkerCount` parallelism; those are
`Infrastructure.Tests/Outbox/OutboxRegistrationTests.cs`'s job instead.

## Coverage

`tests/coverage.runsettings` is the one definition of backend coverage, used by CI's
`backend (Debug)` leg for the PR report and runnable locally (the command is in its header). Each
test project writes its own Cobertura file and ReportGenerator merges them, so a line counts as
covered if a unit **or** an integration test ran it. It leaves out the generated Wolverine
adapters and the EF migrations, so a regenerated adapter or a new migration never moves the
number. Code that is excluded on purpose takes `[ExcludeFromCodeCoverage]` with a reason, not a
new pattern in the runsettings.

## Rules

- Name tests `MethodName_Scenario_ExpectedOutcome`. Architecture and convention tests
  have no method under test, so name those for the rule they enforce instead
  (e.g. `Domain_references_no_other_layer`).
- One behaviour per test. If the name needs "and", split it.
- Assert on behaviour, not on implementation detail.
- Never mock a type you do not own — wrap it in a port and substitute that.
- No `Thread.Sleep`. Inject an `IClock`.
- A test that needs `[Fact(Skip = ...)]` is either deleted or fixed.
- FluentAssertions 7.2.2's `OnlyContain(predicate)` throws on an empty collection
  rather than treating it as vacuously true. Assert on the disallowed subset
  instead (`disallowed.Should().BeEmpty()`), so the check is correct whether or
  not the collection is empty.

## Worker tests

`Worker.IntegrationTests` runs the real job host against Testcontainers Postgres, joining one
collection (`WorkerFactoryCollection`) exactly as `ApiFactoryCollection` and `PostgresCollection`
do — one container for the whole project, never one per class.

Unlike `ApiFactory`, this factory does **not** strip its background machinery out: the Wolverine
listeners are the thing under test, so both lanes stay on. The worker has no outbox pump to strip
(ADR 0028), so a test whose job is enqueued by a domain event handler drains the outbox itself
with `WorkerFactory.DrainOutboxUntilEmptyAsync()`, the same loop as `ApiFactory`'s.

Two rules specific to this project, both learned from failing tests rather than reasoned out:

- **`IncludeExternalTransports()` is required on a tracking session.** A job goes out to a
  RabbitMQ queue and comes back in through the host's own listener, and a tracking session ignores
  external transports by default — without it the session sees the message "Sent", stops waiting,
  and every delivery assertion fails with "No messages of type … were received".
- **Never use a tracking session to prove something did *not* happen.** `ExecuteAndWaitAsync`
  waits for a message to be handled, so a message that must never be handled only ever produces a
  timeout — an absence of evidence, bought at the price of the full timeout. Assert on the stored
  envelope instead. A scheduled job waits in `wolverine.wolverine_incoming_envelopes` with status
  `Scheduled` until due; RabbitMQ has no delayed delivery. Its `message_type` is
  `scheduled-envelope`, not the job's type, so match the job by its body
  (`position('SendOrderConfirmation'::bytea in body) > 0`); a *retry* waiting in the same table
  does carry the job's type.

`ApiPublishesOnlyTests` (in `Api.IntegrationTests`) and `JobDeliveryTests` are two halves of one
rule: the API listens on no job queue, the worker listens on every lane. Neither is decoration —
ADR 0016's whole design rests on that split being true, and nothing else would catch it changing.
