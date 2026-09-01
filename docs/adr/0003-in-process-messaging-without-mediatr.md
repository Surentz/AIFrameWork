# 0003. In-process messaging without MediatR

**Date:** 2026-09-01
**Status:** Accepted

## Context

The backend needs two things a mediator library normally supplies. First, a way for a
controller to reach a use-case handler without naming it. Second, a way for one use case to
trigger side effects in another without the caller knowing about them: an order is placed, and
a confirmation email, a read-model update and an audit row follow. The reflex reach is MediatR.

MediatR bundles three separable jobs — request/response `Send`, notification `Publish`, and
pipeline behaviors — behind one library. `System.Threading.Channels` is BCL and fits exactly
one of those jobs well: a channel is a queue, one-way and asynchronous, and that is what
`Publish`-style fan-out is. It fits `Send` poorly — a request/response round-trip through a
queue needs a `TaskCompletionSource` bridge, adds latency, and risks orphaned completions and
lost stack traces, for no gain over resolving the handler and calling it directly. A queue
between a controller and its handler adds a hop to a call that is already on the same stack.

## Decision

Two separate seams instead of one mediator:

- **The request path** (this plan): a controller reaches its handler through a direct
  in-process dispatcher. No queue, no reflection.
- **The event path** (a later plan): an aggregate raises a domain event, it is persisted with
  the aggregate in an outbox, and a channel drains it to its handlers afterward, at least once.

Only the request path is built. It works like this:

`ICommand<TResponse>` and `IQuery<TResponse>` are phantom-typed marker interfaces — `TResponse`
never appears in a method signature, only in the constraint on `ICommandHandler<TCommand,
TResponse>` / `IQueryHandler<TQuery, TResponse>`. That is what lets a controller write
`dispatcher.SendAsync(new PlaceOrder(...))` and get back a `Result<Guid>` without naming
`PlaceOrderHandler` anywhere.

Recovering the concrete handler type from `command.GetType()` at dispatch time is exactly what
`MakeGenericType`/`MethodInfo.Invoke` is for — and reflection was ruled out. Instead,
`AddCommand<TCommand, TResponse, THandler>()` (and its query equivalent) captures the closed
generic in a `static` local function at registration time, wraps it in a `CommandDescriptor`,
and registers that as a singleton. `CommandRegistry` (and `QueryRegistry` for the query side),
also registered as a singleton, collapses the descriptors into a `Dictionary<Type,
CommandDescriptor>` once, at first resolution — not per request — and injects it into the
still-scoped `CommandDispatcher`. Dispatch is then a dictionary lookup keyed on
`command.GetType()` plus a delegate call — no reflection anywhere on the request path. An
earlier revision of this design built that dictionary directly in `CommandDispatcher`'s own
field initializer; because the dispatcher was registered `AddScoped`, that ran once per HTTP
request rather than once — a review caught it, and `CommandRegistry`/`QueryRegistry` are the
fix.

Two behaviors run inside the registered delegate, commands only: validation (resolves
`IValidator<TCommand>` if one is registered and short-circuits to a failed `Result`), then
unit-of-work commit (`IUnitOfWork.SaveChangesAsync`, exactly once, only on success). Handlers
never call `SaveChangesAsync` themselves — that is what keeps one command to one transaction.
Queries get neither behavior; a query that needs a transaction is a command.

The cost this buys back: a command or query implementing `ICommand<T>`/`IQuery<T>` twice makes
`TResponse` inference ambiguous at every call site, and a command or query with no
`AddCommand`/`AddQuery` call fails at runtime — a dictionary miss — rather than at compile
time. Both are covered by tests in `Infrastructure.Tests`, not by the type system.

## Consequences

The request half of this design is implemented: `ICommandDispatcher`, `IQueryDispatcher`,
`AddCommand`/`AddQuery`, both behaviors, and the registration-completeness tests all exist and
are exercised by the `PlaceOrder`/`GetOrder` use case. The event half — `IDomainEvent`, the
outbox, the `SaveChanges` interceptor, the channel, the poller and the worker pool — is
specified in `docs/superpowers/specs/2026-08-28-in-process-messaging-design.md` §7–§10 but not
built. A reader should not infer that domain events, an outbox, or at-least-once delivery ship
today.

This ADR originally claimed "nothing in this branch touches a database," written one commit
before EF Core, Npgsql, an initial migration, `AiFrameworkDbContext`, and two
Testcontainers-backed test suites (`Infrastructure.Tests` and `Api.IntegrationTests`) landed.
That claim is false at HEAD and is corrected here rather than left standing: persistence exists
in this branch, but only to back `IUnitOfWork.SaveChangesAsync` for the request path's
unit-of-work behavior above — it is not the event-path outbox, which remains unbuilt.

A missing `AddCommand`/`AddQuery` registration fails at runtime, not compile time — the one
place this design is weaker than injecting the handler directly. The registration-completeness
test in `Infrastructure.Tests` is the only thing standing between that gap and a runtime
failure in production; if it is ever deleted or scoped too narrowly, the trade stops paying.

The design also cost two repo-wide analyzer exemptions in `.editorconfig`: `MA0048` (one type
per file), because a command, its validator and its handler are deliberately kept in one file
per feature, and `CA1716` (keyword collisions), not a risk worth renaming public types for in a
C#-only codebase. `ICommand<T>`/`IQuery<T>` also carry a local `#pragma` suppression of `S2326`
each, because `TResponse` is a phantom type parameter that never appears in the interface body
but is load-bearing for call-site inference.

## Alternatives considered

**MediatR.** Rejected for the reason in Context: it forces the one job a channel fits well
(`Publish`) and the one job it fits poorly (`Send`) through the same abstraction, and the
request path pays for a queue hop it does not need.

**Direct handler injection** (`ICommandHandler<PlaceOrder, Guid>` resolved and called directly
by the controller). No dispatcher, no descriptor, compile-time safety restored — but the
controller now names every handler type it calls, and decorating handlers uniformly (behaviors)
needs a library like Scrutor to apply open-generic decorators across every handler
registration. Rejected to keep the controller shape uniform (`SendAsync`, one line) and to keep
validation and unit-of-work in one place rather than repeated per handler.

**Explicit type arguments at every call site**
(`dispatcher.SendAsync<PlaceOrder, Guid>(command)`). Removes the need for `TResponse`
inference and the single-implementation constraint it imposes. Rejected: it pushes an
implementation detail (the response type) onto every caller, for a problem the phantom-type
trick already solves for free.

**Cached reflection, MediatR-style** (`MakeGenericType` once, cache the `MethodInfo`, invoke
via a compiled expression tree). Avoids the descriptor registration boilerplate. Rejected
because it is not reflection-free, only reflection-once — and it breaks under trimming/AOT in
a way the registration-based approach does not, for a boilerplate cost that is near-free since
`/feature` will generate the registration line.

**A channel on the request path too**, for one dispatch mechanism instead of two. Rejected in
Context: `Send` needs a response, and bridging a queue back to a caller's `await` costs a
`TaskCompletionSource` round-trip, orphaned-completion handling, and lost stack traces, for no
gain over calling the handler directly.
