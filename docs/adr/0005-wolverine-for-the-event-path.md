# 0005. Wolverine for the event path, on PostgreSQL, without a broker

**Date:** 2026-09-05
**Status:** Accepted

## Context

Two things forced this decision at the same time.

The first is a licensing shift across the whole .NET messaging ecosystem. MassTransit changed
at v9 — announced April 2025, shipped January 2026 — to a commercial, source-available licence
requiring a runtime licence key. v8 remains Apache-2.0 and is stated to receive security and
critical fixes through at least the end of 2026, with no commitment past that. MediatR made the
same move (this repo had already declined MediatR, for unrelated reasons — see ADR 0003).
KurrentDB, formerly EventStoreDB, moved to the Event Store License v2 at 24.10, which is not
OSI-approved. NServiceBus was already commercial, with a Community edition capped at three
endpoints and 10,000 messages a day. The default answers all became paid answers within about a
year of each other.

MassTransit's under-$1M-revenue tier would very likely cost this project nothing today. It was
still rejected: the wording is that an organisation "may qualify," the discount excludes
commercial support, and it puts a runtime licence key in the deployment path of a project that
currently has no such dependency.

The second is that this repo never adopted MassTransit at all. No `.csproj` references it. So
there was no migration pressure and no exit cost — this was a free choice on merit, and the
honest baseline was "adopt nothing."

That baseline is strong here, because the event path already works. `IDomainEvent` and
`Entity.Raise` live in `Domain`; `DomainEventsInterceptor` persists raised events to the outbox
in the same transaction as the aggregate; `OutboxPoller.ClaimAsync` claims batches with
`FOR UPDATE SKIP LOCKED`, incrementing `Attempts` at claim time so a hard crash cannot loop
forever, and recovering rows from dead workers via `LeasedUntil`; `OutboxWorkItemProcessor` fans
out to `IDomainEventHandler<TEvent>` with a `DomainEventContext` carrying a stable `MessageId`
as the dedupe key. `OutboxDeliveryTests` proves at-least-once delivery end to end, including
idempotent redelivery. What is missing is not delivery. It is sagas and process managers,
scheduled and delayed messages, retry policy as configuration rather than a hand-rolled
`NextAttemptAt` backoff, and a path to cross-process messaging that does not require rewriting
the event path to get there.

A stated goal — "make the backend strong for event sourcing, meaning the backend can act on
events" — conflates two things that were separated before deciding. Acting on events is
event-driven, and is built. Event sourcing, where events become the system of record and current
state is a projection, is a far larger and less reversible commitment, and no aggregate in this
repo yet has a business case demanding it.

## Decision

Adopt **Wolverine** (MIT, 6.33.0 as of 2026-09-03, targets `net10.0`) for the **event path
only**, using PostgreSQL for both durability and transport:

- `WolverineFx.Postgresql` provides the durable inbox/outbox and, via `wolverine_queues`, a
  message transport backed by PostgreSQL itself. **No broker is introduced.** The dev container
  added with the local dev setup remains the only infrastructure this project runs.
- `WolverineFx.EntityFrameworkCore` enrolls the existing `AiFrameworkDbContext` in Wolverine's
  transaction, so application state and outgoing messages commit atomically — the same guarantee
  `DomainEventsInterceptor` hand-builds today. This goes through
  `opts.UseEntityFrameworkCoreTransactions()` and `IDbContextOutbox<AiFrameworkDbContext>`,
  **not** `AddDbContextWithWolverineIntegration`: the latter replaces this repo's own
  `AddDbContext` registration and forces its options to a singleton lifetime, which would mean
  giving up the `AddInterceptors(DomainEventsInterceptor)` wiring the existing outbox depends on.
  The chosen route is purely additive, which is what lets both paths run at once.
- `Marten` is deliberately **not** adopted. Wolverine's PostgreSQL durability does not require
  it.

**The request path stays exactly as ADR 0003 built it.** Wolverine offers a mediator and it is
declined. `ICommandDispatcher`, `IQueryDispatcher`, `CommandRegistry`/`QueryRegistry`, and the
validation and unit-of-work behaviors keep their current shape. ADR 0003 drew a seam between the
request path and the event path; this decision changes one side of that seam and leaves the
other untouched.

**Production uses static code generation.** Wolverine generates handler adapters with Roslyn. As
of 6.0, core `WolverineFx` no longer references `JasperFx.RuntimeCompiler`; production runs
`TypeLoadMode.Static` against code pre-generated at build time by `dotnet run -- codegen write`,
and the runtime compiler package is referenced in Debug configurations only.

The existing outbox is **not deleted on adoption**. Wolverine lands alongside it, both green, and
the hand-built implementation is retired only after the replacement is proven against it.

Event sourcing is **deferred, not rejected**. If a specific aggregate later earns it, Marten is
the intended vehicle — it is MIT, PostgreSQL-native, and its `Marten.EntityFrameworkCore`
projections (`EfCoreSingleStreamProjection`, `EfCoreMultiStreamProjection`) can project an
event-sourced stream into the EF Core tables this repo already owns, atomically. That keeps the
experiment scoped to one aggregate rather than a rewrite.

## Consequences

**What this makes easy.** Sagas and process managers become available for multi-step workflows,
which the current outbox cannot express — it fans out single events and stops. Scheduled and
delayed messages ("retry in four hours", "expire this tomorrow") become configuration. Retry and
circuit-breaker policy moves out of hand-written backoff arithmetic. And because transport is a
configuration concern in Wolverine, moving to RabbitMQ or Azure Service Bus later is a
registration change rather than a redesign of the event path.

**What this costs, concretely.**

- **A new build step.** `dotnet run -- codegen write` must run in CI before publish, and the
  generated code must stay in sync with handlers. Forget it and production either falls back to
  runtime compilation it no longer has the package for, or runs stale adapters. This is the
  single largest ongoing cost of the decision.
- **Roslyn stays out of the image only by discipline.** The 100MB-class dependency is Debug-only
  by configuration, not by construction. This repo has already paid this exact tax once, when
  `EFCore.Design` in `Api.csproj` took the publish output from 7.9MB to 37MB. A `PrivateAssets`
  attribute will not save us here either — the guard is the conditional package reference and
  nothing else.
- **Deleting working, understood code.** `OutboxPoller` and `OutboxWorkItemProcessor` are
  correct, tested, and fully understood by their author. Replacing them trades comprehension for
  capability, and the concurrency-sensitive parts — lease recovery and increment-at-claim — move
  from code we can read to a dependency we trust.
- **Wolverine owns tables in our database.** Envelope storage and `wolverine_queues` are
  provisioned by the library, outside `dotnet ef migrations`. Two schema authorities now exist in
  one database, and the EF startup-project/design-time-factory arrangement gains a neighbour it
  does not know about. The spike puts them in a dedicated `wolverine` schema rather than beside
  the EF tables, so the two authorities do not argue over `public`.
- **The application no longer starts without a reachable database.** Found by the spike, not
  predicted by this ADR: durable Wolverine migrates its envelope schema *while the host starts*,
  so a host pointed at an unreachable database now fails to boot instead of starting and failing
  on first use. `HealthTests` caught it immediately — it deliberately runs with a placeholder
  connection string and no container, because `/health` never touches Postgres, and it began
  failing with `Failed to connect to 127.0.0.1:5432`. This is a change to the application's
  startup contract and it affects more than tests: any environment where the app is expected to
  come up and report unhealthy, rather than not come up at all, is affected. The mitigation is
  `DurabilityMode.MediatorOnly`, which disables envelope storage and with it the startup
  connection; the spike exposes it as `Wolverine:Durable` configuration, defaulting to durable.
- **Open core is a bet, not a guarantee.** JasperFx keeps the libraries MIT and sells CritterWatch
  ($4,000/yr Professional, $7,500/yr Enterprise) and an AI Skills library ($250–$2,000/yr)
  alongside them. That is a materially safer structure than licensing the library itself, but
  MassTransit, MediatR, and Event Store all demonstrate that a vendor's licence can change. The
  mitigation is that the event path is one seam, and `IDomainEvent` / `IDomainEventHandler<TEvent>`
  stay ours.
- **An unused half.** Wolverine's mediator sits in the dependency and goes deliberately unused, so
  a future reader will find two dispatch mechanisms in the codebase and must be told why. This ADR
  is that telling.

**What this rules out.** Not event sourcing — that is deferred and Marten remains the intended
route. It does rule out staying dependency-free on the event path, and it rules out MassTransit
as a later default, since the two would overlap almost entirely.

## Alternatives considered

**Keep the hand-built outbox and adopt nothing.** The strongest alternative, and the correct
answer if sagas and scheduled messages are not needed in the near term. It works, it is tested by
`OutboxDeliveryTests`, and it costs no new dependency, no build step, and no schema authority.
Rejected on the grounds that the missing capabilities are wanted, and that hand-maintaining
lease-and-claim concurrency indefinitely is a real, permanent cost — but this was a close call,
not a rout, and it is the alternative to revisit first if Wolverine disappoints.

**MassTransit v9.** The obvious incumbent, and probably free for this project under the
under-$1M-revenue tier. Rejected for the runtime licence key in the deployment path, the "may
qualify" conditionality, and the exclusion of commercial support from the free tier — all in
exchange for capabilities Wolverine provides under MIT with no key at all.

**MassTransit v8, pinned.** Apache-2.0 and functional today. Rejected as a knowingly terminal
choice: security and critical fixes are promised only through the end of 2026, and adopting a
dependency onto a stated end-of-life clock is worse than adopting nothing.

**OpenTransit, the community fork of MassTransit v8.** Rejected on maintenance risk. A young fork
carrying a large surface area, without the evidence of sustained maintenance that would justify
putting the event path on it.

**NServiceBus.** Mature and well-supported. Rejected: commercial, and the free Community edition
caps at three endpoints and 10,000 messages a day — limits that would need re-evaluating at
exactly the moment the system starts mattering.

**Rebus.** MIT, genuinely free at any size, and a legitimate choice. Rejected as the narrower one:
it is a service bus, where Wolverine additionally brings the EF Core transactional integration,
the PostgreSQL transport that avoids standing up a broker, and a route to Marten if event sourcing
is ever taken up.

**Brighter and CAP.** Both open-source and viable. Not pursued in depth once Wolverine's
PostgreSQL-transport-plus-EF-Core-transaction combination proved to match this repo's shape
exactly; revisit if Wolverine's code generation becomes intolerable.

**Marten, and full event sourcing now.** Rejected as premature. Event sourcing is a one-way door
on modelling, querying, and schema evolution, and no aggregate here has yet earned it. Deferred to
a single-aggregate trial via EF Core projections, as described above.

**KurrentDB (formerly EventStoreDB).** A purpose-built event store. Rejected on both licence
(ESLv2, not OSI-approved, from 24.10) and infrastructure — it means a second database beside
PostgreSQL, which is a large price for a capability not yet needed.
