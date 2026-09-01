# In-Process Messaging — Command Dispatch and Domain Events — Design

**Date:** 2026-08-28
**Status:** Approved, not yet implemented

## 1. Context

The backend needs two things a mediator library normally supplies. First, a way for a
controller to reach a use-case handler without naming it. Second, a way for one use case to
trigger side effects in another without the caller knowing about them: an order is placed, and
a confirmation email, a read-model update and an audit row follow. The reflex reach is MediatR.

This design does not use MediatR. It uses `System.Threading.Channels` for the part a channel
actually fits, and a direct call for the part it does not.

The two seams are deliberately separate. **§6** is the request path: a controller awaits a
command handler in-process. **§7–§9** are the event path: an aggregate raises an event, it is
persisted with the aggregate, and a channel drains it to its handlers afterwards.

`src/` contains no `.cs` files at the time of writing — only the per-layer `CLAUDE.md`
scaffolding from the framework design. There is nothing to migrate; this is a choice made
before the first feature exists, not a refactor.

### Why not MediatR

MediatR bundles three separable jobs. A channel is a queue: one-way, asynchronous, with
backpressure. It fits exactly one of them.

| MediatR job | Channel fit |
|---|---|
| `Send<TResponse>` — request → response | Poor. Needs a `TaskCompletionSource` round-trip per request: added latency, orphaned-completion and timeout handling, lost stack traces — for no gain over resolving the handler and calling it. |
| `Publish` — notification fan-out | Good. This is what a channel is. |
| Pipeline behaviors | Neutral. DI decoration, independent of transport. |

So the request path keeps a direct call, and only domain events ride the channel.

## 2. Goals

- A controller reaches its handler through one line, without naming the handler type, and
  without reflection.
- Validation and transaction boundaries live in one place, not repeated in every handler.
- Domain events raised by aggregates reach their handlers **at least once**, surviving a
  process crash.
- Events are written atomically with the aggregate that raised them. There is no state in
  which the order exists and the event does not.
- Dispatch from a persisted string name to a strongly-typed handler with **no reflection**
  at dispatch time.
- Every layer boundary the design crosses is already legal under the dependency rule. The
  `dependency-rule.ps1` hook needs no change.
- A poison message can never block the pipeline, and is never silently discarded.

## 3. Non-goals

- **No request/response through a channel.** The request path is a direct in-process call
  through a thin dispatcher (§6).
- **No deferred-job queue.** Domain events only. A job queue is a different shape — single
  consumer, caller observes completion — and is not built here.
- **No cross-process transport.** No RabbitMQ, no Service Bus, no Kafka. In-process only.
- **No ordering guarantee.** See D6.
- **No alerting on dead messages.** Deferred; see §13.

## 4. Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | Two seams: direct dispatch on the request path, channel dispatch for events | User choice. A queue between a controller and its handler adds a hop to a call already on the same stack. |
| D2 | Domain events only ride the channel | User choice. Fan-out is the one MediatR role a channel improves on. |
| D3 | Transactional outbox, at-least-once delivery | User choice over best-effort in-memory. Costs an outbox table, a `SaveChanges` interceptor, and a poller with lease and retry. Makes handler idempotency mandatory, not advisory. |
| D4 | Explicit registration with a stable string name: `AddDomainEvent<T>("order.placed")` | User choice over reflection scanning and a source generator. Greppable, trim/AOT-safe, and a class rename cannot orphan unprocessed rows. The boilerplate is near-free because `/feature` generates it. |
| D5 | Parallel workers, exponential backoff, dead-letter after `MaxAttempts` | User choice. A poison message cannot block the pipeline; dead rows stay in the table as the signal. |
| D6 | No ordering guarantee | Follows from D5. Per-row retry already breaks ordering: a row that fails and retries in 30s is overtaken by the rows behind it. "Ordered" and "retryable" are compatible only if a failure blocks its partition. |
| D7 | Ports in `Application`, all machinery in `Infrastructure` | Every arrow is already legal: `Application → Domain` for event types, `Infrastructure → Application` to resolve handlers, `Api → Infrastructure` for DI. No new exception to the matrix. |
| D8 | PostgreSQL, `FOR UPDATE SKIP LOCKED` | User choice. The standard queue-claim idiom, and Testcontainers has a first-class module for the tests in §11. |
| D9 | Retry granularity is the message, not the handler | The interceptor cannot know the handler count at save time — handlers are resolved at dispatch. One row per (event × handler) would fix this at a cost not worth paying. Consequence: a partially-failed fan-out re-runs its successful handlers. |
| D10 | `attempts` increments at claim time, not on failure | Otherwise a message that hard-crashes the worker loops forever without ever exhausting its budget. |
| D11 | Events carry no timestamp | `Domain` has no clock; `IClock` is an `Application` port and that arrow does not run backwards. The interceptor stamps `OccurredAt` on the outbox row. Keeps events pure data and trivially testable. |
| D12 | One file-scoped CA1031 exemption, for the worker | User choice over a `#pragma` at the call site. A worker loop must catch everything or the pump dies one worker at a time. Visible and greppable in `.editorconfig`; root `CLAUDE.md` is amended because "no exemption" stops being true. |
| D13 | The handler signature carries a `DomainEventContext` | At-least-once makes idempotency mandatory, so handlers need a stable dedupe key. `MessageId` is constant across redeliveries; `Attempt` lets a handler degrade on retry. |
| D14 | Processed rows are pruned by the poller | An outbox that only grows is a slow outage. |
| D15 | Registration-based command dispatcher: `AddCommand<TCommand, TResponse, THandler>()` | User choice over direct handler injection, explicit type arguments, and cached reflection. Keeps D4's no-reflection stance on the request path and reuses its exact registration idiom. Costs one cast, one boxed result, and a runtime — not compile-time — failure for a missing registration, which §11 covers with a test. |
| D16 | Separate `ICommandDispatcher` and `IQueryDispatcher` | Only commands carry the unit-of-work behavior. The split makes a query that writes visible at the call site rather than buried in a handler. |
| D17 | Two behaviors, commands only: validation then unit of work | User choice over validation-only and pure pass-through. Handlers never call `SaveChangesAsync`, so one command is exactly one transaction — which is what makes the outbox interceptor fire once with the complete event set. |

## 5. Layer placement

| Layer | Owns |
|---|---|
| `Domain` | `IDomainEvent`, `Entity.Raise()`, the pending-event list on the aggregate |
| `Application` | `ICommand<T>` / `IQuery<T>`, the handler and dispatcher interfaces, `IDomainEventHandler<TEvent>`, `DomainEventContext`, and every handler |
| `Infrastructure` | Dispatcher implementations, command registry and behaviors; outbox entity and EF configuration, `SaveChanges` interceptor, event registry, channel, poller, worker pool, retry, pruning |
| `Api` | `AddCommand<...>()` and `AddDomainEvent<T>("name")` registration lines in the composition root, nothing else |

The dispatcher *implementations* sit in `Infrastructure` alongside the event machinery, even
though nothing in them touches EF. They resolve handlers from the container and the
unit-of-work behavior calls `IUnitOfWork` — both composition concerns, and `Application`
declaring its own dispatcher implementation would have it reach for
`Microsoft.Extensions.DependencyInjection`, which `Domain` bans and `Application` has no
business with either.

Rejected placements:

**Dispatcher core in `Application`.** `System.Threading.Channels` is BCL, so `Application`
could legally own the channel. But the worker pool is a `BackgroundService`, which drags
`Microsoft.Extensions.Hosting` into `Application`, and lease semantics are inescapably a
database concern. The transport would be split across two layers for no gain.

**A fifth `src/Messaging` project.** Reusable and independently testable, and it would keep
`Infrastructure` from growing a second personality. Rejected because the four-layer structure
was reaffirmed immediately before this design. Revisit only if `Infrastructure` becomes
unwieldy.

## 6. The request path

Commands and queries never touch the channel (D1). A controller awaits its handler on the
same stack; a queue in between would add a hop to a call already in flight.

### 6.1 Contracts

```csharp
namespace AiFramework.Application.Abstractions;

public interface ICommand<TResponse>;
public interface IQuery<TResponse>;

public interface ICommandHandler<in TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResponse> where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

public interface ICommandDispatcher
{
    Task<Result<TResponse>> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken ct);
}

public interface IQueryDispatcher
{
    Task<Result<TResponse>> SendAsync<TResponse>(IQuery<TResponse> query, CancellationToken ct);
}
```

Split rather than a single `IDispatcher`, because only commands carry the unit-of-work
behavior (§6.3) — and the split makes a query that writes visible at the call site.

### 6.2 Registration, and the type-inference problem

Given an `ICommand<TResponse>`, the dispatcher needs `ICommandHandler<TCommand, TResponse>`
with the **concrete** command type. Recovering it from the interface is precisely what
`MakeGenericType` is for, and D4 ruled reflection out. So the command side uses the same trick
as the event side: capture the closed generic at registration time.

```csharp
public static IServiceCollection AddCommand<TCommand, TResponse, THandler>(
    this IServiceCollection services)
    where TCommand : ICommand<TResponse>
    where THandler : class, ICommandHandler<TCommand, TResponse>
{
    static async Task<object?> InvokeAsync(IServiceProvider sp, object command, CancellationToken ct)
    {
        var handler = sp.GetRequiredService<ICommandHandler<TCommand, TResponse>>();
        return await handler.HandleAsync((TCommand)command, ct).ConfigureAwait(false);
    }

    services.AddScoped<ICommandHandler<TCommand, TResponse>, THandler>();
    return services.AddSingleton(new CommandDescriptor(typeof(TCommand), InvokeAsync));
}
```

The dispatcher keys a `Dictionary<Type, CommandDescriptor>` on `command.GetType()`, invokes
the delegate, and casts the result back:

```csharp
public async Task<Result<TResponse>> SendAsync<TResponse>(
    ICommand<TResponse> command, CancellationToken ct)
{
    if (!_descriptors.TryGetValue(command.GetType(), out var descriptor))
        throw new InvalidOperationException(
            $"No handler registered for '{command.GetType().Name}'.");

    return (Result<TResponse>)(await descriptor
        .Invoke(_serviceProvider, command, ct).ConfigureAwait(false))!;
}
```

One cast and one boxed `Result<TResponse>` per command, both contained in this method. In
exchange there is no reflection anywhere on the request path, and the registration idiom is
identical to `AddDomainEvent<T>` — one pattern for the whole codebase rather than two.

`TResponse` is inferred from the argument, so call sites stay clean. The constraint this
carries: a command may implement `ICommand<T>` **exactly once**. Implementing it twice makes
inference ambiguous and breaks every call site. `src/Application/CLAUDE.md` must say so.

A missing `AddCommand` registration is a runtime throw rather than a compile error — the one
place this is weaker than injecting the handler directly. §11 closes it with a test that
asserts every `ICommand<>` in the assembly has a descriptor.

### 6.3 Behaviors

Two, both command-only, applied inside the registered delegate in this order:

1. **Validation.** Resolves `IValidator<TCommand>` from FluentValidation if one is registered
   and short-circuits to a failed `Result` before the handler runs. No validator registered
   means no validation — absence is not an error.
2. **Unit of work.** On a successful `Result`, calls `IUnitOfWork.SaveChangesAsync` exactly
   once. Handlers never call it themselves.

The unit-of-work behavior is what makes the outbox guarantee concrete: one command is one
transaction, so the interceptor (§9.2) fires once with the complete set of events that command
raised. A handler calling `SaveChangesAsync` twice would split its events across two
transactions — each atomic on its own, but the command as a whole no longer is.

Queries get neither behavior. A query that needs a transaction is a command.

### 6.4 The controller

```csharp
[HttpPost]
public async Task<IActionResult> Place(PlaceOrderRequest request, CancellationToken ct)
{
    var result = await _commands.SendAsync(new PlaceOrder(request.Sku, request.Quantity), ct);
    return result.ToActionResult();
}
```

Bind, dispatch, map — the shape `src/Api/CLAUDE.md` already requires. The controller never
names a handler type, never opens a transaction, and never publishes an event: events come
from the aggregate, through the interceptor.

## 7. Domain

```csharp
namespace AiFramework.Domain.Common;

/// <summary>Marker for something that happened in the domain.</summary>
public interface IDomainEvent;

public abstract class Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
```

Events are pure data records:

```csharp
public sealed record OrderPlaced(Guid OrderId, decimal Total) : IDomainEvent;
```

`ClearDomainEvents()` is public because the interceptor, in another assembly, must call it.
The alternative — `internal` plus `InternalsVisibleTo` — buys encapsulation at the cost of a
compile-time coupling between `Domain` and `Infrastructure` that the dependency rule exists
to prevent.

## 8. Application

```csharp
namespace AiFramework.Application.Abstractions;

public readonly record struct DomainEventContext(Guid MessageId, int Attempt);

public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, DomainEventContext context, CancellationToken cancellationToken);
}
```

Handlers live with their feature: `Application/Orders/EventHandlers/SendConfirmationEmail.cs`.
They depend on ports, never on `DbContext`.

**Handlers must be idempotent.** This is not a style preference. At-least-once delivery makes
redelivery normal, and D9 means a partially-failed fan-out re-runs its successful handlers.
`context.MessageId` is the dedupe key: stable across every redelivery of the same event.

## 9. Infrastructure

### 9.1 The outbox

```csharp
internal sealed class OutboxMessage
{
    public required Guid Id { get; init; }
    public required string EventName { get; init; }   // "order.placed"
    public required string Payload { get; init; }     // JSON
    public required DateTimeOffset OccurredAt { get; init; }
    public OutboxStatus Status { get; set; }          // Pending | InFlight | Processed | Dead
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LeasedUntil { get; set; }
    public string? LastError { get; set; }
}
```

Mapped in an `IEntityTypeConfiguration<OutboxMessage>` like every other entity. A filtered
index on `(Status, NextAttemptAt)` over `Pending` rows serves the claim query.

`Status` is persisted **as a string**, via `HasConversion<string>()`. The claim query in §9.5
is raw SQL comparing against `'Pending'` and `'InFlight'`, so storing the enum as an ordinal
would silently mismatch. A string column also survives someone reordering the enum members.

### 9.2 The interceptor

A `SaveChangesInterceptor.SavingChangesAsync` walks `ChangeTracker.Entries<Entity>()`,
converts each pending event into an `OutboxMessage` **added to the same `DbContext`**, then
clears the aggregate's list.

Because the rows are added before the save, EF writes them in the same transaction as the
aggregate. That is the entire atomicity guarantee, and it is why this must be
`SavingChangesAsync` and not `SavedChangesAsync`.

`OccurredAt` comes from the injected `IClock` (D11).

An event type with no `AddDomainEvent<T>` registration throws at save time, naming the type.
This cannot be caught at startup — nothing enumerates which events a handler might raise — so
failing loudly at the write is the earliest honest point.

### 9.3 The registry

```csharp
public sealed record DomainEventDescriptor(
    string Name,
    Type EventType,
    Func<IServiceProvider, string, DomainEventContext, CancellationToken, Task> Dispatch);
```

```csharp
public static IServiceCollection AddDomainEvent<TEvent>(this IServiceCollection services, string name)
    where TEvent : IDomainEvent
{
    static async Task DispatchAsync(IServiceProvider sp, string json, DomainEventContext ctx, CancellationToken ct)
    {
        var e = JsonSerializer.Deserialize<TEvent>(json, OutboxJson.Options)
                ?? throw new InvalidOperationException(
                       $"Payload for '{typeof(TEvent).Name}' deserialised to null.");

        foreach (var handler in sp.GetServices<IDomainEventHandler<TEvent>>())
            await handler.HandleAsync(e, ctx, ct).ConfigureAwait(false);
    }

    return services.AddSingleton(new DomainEventDescriptor(name, typeof(TEvent), DispatchAsync));
}
```

The `static` local function captures nothing — no closure allocation — but `TEvent` still
comes from the enclosing generic method's type parameter. The registry collapses the
descriptors into a `Dictionary<string, DomainEventDescriptor>` once at startup. Dispatch is a
dictionary lookup and a delegate call: no `MakeGenericType`, no `MethodInfo.Invoke`, nothing
that defeats trimming.

Handlers for one event run **sequentially**, in DI registration order.

### 9.4 The channel

```csharp
Channel.CreateBounded<OutboxWorkItem>(new BoundedChannelOptions(options.ChannelCapacity)
{
    FullMode     = BoundedChannelFullMode.Wait,
    SingleWriter = true,   // one poller
    SingleReader = false,  // worker pool
});

internal readonly record struct OutboxWorkItem(Guid Id, string EventName, string Payload, int Attempt);
```

A record struct crosses the channel, never the EF entity: a tracked entity belongs to the
scope that loaded it, and handing one to another scope's worker is a defect waiting to happen.

`FullMode.Wait` is the reason a channel earns its place. When the workers saturate, the
poller's `WriteAsync` blocks, so it stops claiming rows, and **the database stays the buffer
instead of memory**. Hand-rolling that needs a `BlockingCollection` or a semaphore.

### 9.5 The poller

A `BackgroundService` that claims a batch atomically, writes it to the channel, and delays
only when a batch comes back empty.

```sql
UPDATE outbox SET status = 'InFlight', leased_until = @until, attempts = attempts + 1
WHERE id IN (
    SELECT id FROM outbox
    WHERE (status = 'Pending'  AND (next_attempt_at IS NULL OR next_attempt_at <= @now))
       OR (status = 'InFlight' AND leased_until < @now)      -- reclaim after a crash
    ORDER BY occurred_at
    LIMIT @batch
    FOR UPDATE SKIP LOCKED
)
RETURNING id, event_name, payload, attempts;
```

`FOR UPDATE SKIP LOCKED` makes the claim safe across multiple application instances. The
lease-expiry clause recovers rows from a worker that died mid-handler — this is what makes the
crash guarantee real rather than nominal.

The poller also prunes: on a longer interval, delete `Processed` rows older than
`RetentionPeriod` (D14). `Dead` rows are never pruned.

### 9.6 The workers

`WorkerCount` loops, each `await foreach (var item in reader.ReadAllAsync(ct))`. The channel
handles distribution. Per item:

1. Open an async DI scope.
2. Look up the descriptor by `EventName`. An unknown name goes straight to `Dead`; no number
   of retries will invent a registration.
3. Dispatch with `new DomainEventContext(item.Id, item.Attempt)`.
4. Success → `Processed`. Failure → `Pending` with `NextAttemptAt = now + backoff`, or `Dead`
   with `LastError` once `Attempt >= MaxAttempts`.

Backoff is exponential with jitter, capped: `min(2^attempt seconds, MaxBackoff)`.

On shutdown the writer completes and the workers drain. Anything still `InFlight` is recovered
by lease expiry on the next start.

### 9.7 Options

`OutboxOptions`: `PollInterval`, `BatchSize`, `ChannelCapacity`, `WorkerCount`, `MaxAttempts`,
`LeaseDuration`, `MaxBackoff`, `RetentionPeriod`, `PruneInterval`.

Validated at startup with `ValidateOnStart`. `LeaseDuration` must comfortably exceed the
slowest expected handler, or a healthy message is reclaimed and processed twice.

## 10. The CA1031 exemption

A worker loop must catch every exception. If it does not, one throwing handler kills that
worker, and the pool dies one worker at a time until nothing drains — with no record against
the row that caused it.

Root `CLAUDE.md` currently states that CA1031 is "a global error with no exemption". The
`IExceptionHandler` escapes it by receiving the exception as a parameter; a channel worker has
no such out. So this is the first genuine exemption in the codebase.

It is taken as a **file-scoped `.editorconfig` entry** covering the worker file only, with a
comment naming this spec. Root `CLAUDE.md` is amended in the same change, because the sentence
promising no exemptions stops being true.

## 11. Testing

The structural move that makes this testable: split the **pump** from the **processing**.
`OutboxWorkItemProcessor.ProcessAsync(item, ct) → OutboxOutcome` holds all the decision logic;
the `BackgroundService` loop stays thin enough to need no test of its own.

| Project | Tests |
|---|---|
| `Domain.Tests` | `Raise()` appends; `DomainEvents` is read-only; `ClearDomainEvents()` empties. Pure. |
| `Application.Tests` | Handler behaviour with NSubstitute ports, plus **an idempotency test per handler**: called twice with the same `DomainEventContext`, one side effect. |
| `Infrastructure.Tests` | The real weight, on Testcontainers PostgreSQL |
| `Api.IntegrationTests` | `POST /orders` returns the mapped status code **and** leaves an outbox row with the expected `EventName` |

The request path needs three tests of its own, in `Infrastructure.Tests` (where the dispatcher
implementations live):

- **Registration completeness.** Scan the `Application` assembly for every `ICommand<>` and
  `IQuery<>` implementation and assert each has a descriptor. This is the compile-time safety
  D15 gives up, bought back as a test — and it is the reason the trade in D15 is acceptable.
  Reflection is fine here; it runs in a test, not on the request path.
- **Validation short-circuits.** A command failing its validator returns a failed `Result`
  and the handler is never invoked.
- **Unit of work.** A successful `Result` calls `SaveChangesAsync` exactly once; a failed
  `Result` does not call it at all.

`Infrastructure.Tests` must also cover, for the event path:

- **Atomicity.** Force `SaveChangesAsync` to fail; assert no outbox row exists. This single
  test proves the design's premise. Without it, a passing suite only shows that both writes
  happened to succeed.
- **`SKIP LOCKED`.** Two concurrent claims return disjoint sets.
- **Lease expiry.** An `InFlight` row past `LeasedUntil` is reclaimed.
- **Backoff.** A failure sets `NextAttemptAt` per the curve, driven by a fake `IClock`.
- **Exhaustion.** Because `attempts` increments at claim time (D10), a row claimed for the
  `MaxAttempts`-th time and failing is marked `Dead` — and kept, never deleted.
- **Unregistered event.** The interceptor throws at save, naming the type.
- **Pruning.** `Processed` rows past retention are deleted; `Dead` rows are not.

`tests/CLAUDE.md` bans `Thread.Sleep`, and that rule bites hard here. Every timing assertion
goes through a fake `IClock`, and the channel is driven directly rather than waited on. There
is no "sleep 200ms and hope the worker ran" anywhere in this suite.

## 12. Documentation changes

| File | Change |
|---|---|
| `CLAUDE.md` (root) | Amend the CA1031 sentence: "no exemption" becomes one named, listed exemption. Add the two messaging seams and the idempotency rule to Non-negotiables. |
| `src/Domain/CLAUDE.md` | Events are pure data records here; no clock, no timestamp; raise via `Raise()`. |
| `src/Application/CLAUDE.md` | `ICommand<T>` / `IQuery<T>` and their handlers; **a command implements `ICommand<T>` exactly once**; handlers never call `SaveChangesAsync` (the behavior does); `IDomainEventHandler<TEvent>` with handlers under `<Feature>/EventHandlers/`; idempotency is mandatory; never inject a `DbContext`. |
| `src/Infrastructure/CLAUDE.md` | Dispatcher implementations and behaviors; outbox entity and configuration, interceptor, poller and workers; where the CA1031 exemption lives and why. |
| `src/Api/CLAUDE.md` | Controllers bind → `SendAsync` → map `Result`; `AddCommand<...>()` and `AddDomainEvent<T>("name")` belong in the composition root; controllers never publish events directly. |
| `tests/CLAUDE.md` | Dispatcher and outbox test placement; the registration-completeness test; fake `IClock` and direct channel driving instead of sleeps. |
| `.claude/commands/feature.md` | `/feature` scaffolds the command, handler, `AddCommand` line, event record, `AddDomainEvent` line, event handler, and the idempotency test. |
| `.editorconfig` | The file-scoped CA1031 exemption. |
| `docs/adr/0003-in-process-messaging-without-mediatr.md` | New ADR recording both seams and the rejected alternatives. |

`.claude/hooks/dependency-rule.ps1` needs **no** change. Every arrow this design uses is
already legal under the matrix.

## 13. Risks and open items

- **A forgotten `AddCommand` fails at runtime, not at compile time** (D15). The
  registration-completeness test in §11 is the only thing standing between that and a 500 in
  production. If that test is ever deleted or scoped too narrowly, D15's trade stops paying.
- **`LeaseDuration` is a tuning hazard.** Set below the slowest handler's runtime, a healthy
  message is reclaimed and processed concurrently by a second worker. Idempotency contains the
  damage; it does not prevent the duplicate work.
- **D9's re-run of successful handlers** is the design's sharpest edge and the most likely
  source of a production surprise. It is documented in three places for that reason.
- **Postgres-specific SQL** in the claim. Moving engines means rewriting §9.5. Accepted under
  D8 rather than paying for an `IOutboxStore` abstraction now.
- **The outbox is a high row-churn table.** At volume it will need autovacuum tuning or
  partitioning. Not addressed here.

### Deferred to a later pass

- **Alerting on `Dead` rows.** A health check or metric on the dead-row count. Dead rows are
  currently discoverable only by querying the table.
- **A replay mechanism** for dead messages, once the failure modes are understood.
- **Pipeline behaviors** around handler dispatch — logging, tracing, transaction scope. The
  descriptor delegate is the natural seam; nothing is built until a second handler needs it.
- **A deferred-job queue.** A different shape, explicitly out of scope under §3.
