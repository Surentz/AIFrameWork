# Infrastructure

Implements the ports that Application declares. This is the only layer that knows EF exists.

## Belongs here

`DbContext`, `IEntityTypeConfiguration<T>` classes, repository implementations,
migrations, HTTP clients for external services, and the DI registration extension
this layer exposes to Api.

## Never appears here

Any `AiFramework.Api` namespace. Blocked by the dependency-rule hook.

## Messaging

Dispatchers, descriptors, registration and behaviors live in `Messaging/`. `AddMessaging()`
in `InfrastructureRegistration.cs` is the entry point.

- Dispatch is **reflection-free**. `AddCommand<TCommand, TResponse, THandler>()` (and its
  query equivalent) captures the closed generic in a `static` local function at registration
  time, so dispatch at request time is a dictionary lookup keyed on `command.GetType()` plus
  a delegate call. Do not "simplify" this into `MakeGenericType`/`MethodInfo.Invoke` — the
  reflection-free, trim-safe dispatch is the point.
- Every command and query must be registered in `AddMessaging()`. A registration-completeness
  test (`Infrastructure.Tests`) fails the build if one is missed — it is the only thing
  standing in for the compile-time safety this trade gives up, and it must never be deleted.
- Validators are registered explicitly (`services.AddScoped<IValidator<T>, ...>()`), not by
  assembly scanning, to keep the same greppable posture as command/query registration.
- Commands get validation, then the handler, then unit-of-work commit, then cache eviction.
  Queries get exactly one behavior — the cache — and no transaction and no validation: a query
  that needs a transaction is a command, and a query that needs validation validates its own
  inputs inside its handler (`GetOrdersHandler` is the example).

## Caching

`Caching/` holds the store and its options; the two behaviors live in `Messaging/Behaviors.cs`
beside the command ones. Off by configuration everywhere it is not the subject — see root
`CLAUDE.md` and ADR 0009.

- `Behaviors.CachedAsync` is the whole query pipeline: `AddQuery`'s `static` local function
  delegates to it instead of resolving the handler itself, and handler resolution moved into
  `Behaviors.Handle` so the cached and uncached paths share one definition. `Behaviors.EvictAsync`
  is one line in `AddCommand`, immediately after `CommitAsync`, so eviction happens in-request and
  only after a transaction that succeeded.
- **`CacheScope` is the single source of key and tag composition, and must stay so.** The tag is
  the key's own prefix by construction, which is the entire eviction mechanism. Compose either
  string anywhere else and the two sides drift: eviction simply stops matching, with no exception
  and no log, while every other test stays green.
- Keys use `typeof(TQuery).Name` — the **simple** name — because `IInvalidatesCache.Tags` is
  `nameof(TheQuery)`, which is also simple. Do not "improve" one side to `FullName`; that breaks
  the match. A completeness test keeps the simple names unique so the collision that choice allows
  cannot arrive unnoticed.
- The cache stores the success **value**, never `Result<T>`: `Result<T>.Error` throws on a success,
  so serializing one fails, and its `internal` constructor makes deserializing one impossible.
- A failed `Result` throws a file-private sentinel out of the cache factory, so `HybridCache`
  stores nothing. Do not replace it with a cached wrapper plus a removal — that leaves a window in
  which a concurrent caller reads the failure.
- `CacheOptions` is bound in `Program.cs`, not in `AddCaching`. Binding here would make
  `IOptions<CacheOptions>` depend on an `IConfiguration` being registered, which a bare
  `ServiceCollection` in a unit test does not have — and `CachedAsync` resolves those options on
  every query, so that would break every unit test that dispatches one.
- The cache factory runs **without the caller's ambient `HttpContext`** — `HybridCache` shares one
  factory's work across concurrent callers, so it must not depend on any one caller's context.
  Anything resolved inside it must therefore be safe to read off the request's execution context;
  `ICurrentUser` is, because `CurrentUser` memoizes. Do not add a behavior or a handler dependency
  that reads ambient state.

## Outbox

`Outbox/` implements the event half of the messaging design: an aggregate raises a domain
event, it is persisted with the aggregate, and it is delivered at least once afterward.

- `DomainEventsInterceptor` is an `EF` `SaveChangesInterceptor` that copies pending domain
  events off tracked `Entity` instances onto `OutboxMessage` rows and calls
  `ClearDomainEvents()`. It overrides `SavingChangesAsync`, not `SavedChangesAsync`, and must
  stay there: `SavingChangesAsync` runs before the save, so the outbox insert lands in the same
  `SaveChangesAsync` call — and the same transaction — as the aggregate's own write.
  `SavedChangesAsync` runs after the save has already committed, so a write there is a second,
  unrelated transaction: an aggregate could commit with no outbox row, or vice versa, and the
  atomicity guarantee is gone.
- `OutboxPoller.ClaimAsync` claims due rows with `FOR UPDATE SKIP LOCKED` and increments
  `Attempts` at claim time, not on failure — incrementing only on failure would let a message
  that hard-crashes the process mid-handler loop forever without ever being counted against
  `MaxAttempts`.
- `OutboxWorkItemProcessor` owns the outcome of one claimed item: dispatch, retry with backoff,
  or dead-letter. All of that decision logic lives here rather than in a `BackgroundService` loop
  so it is testable without a host.
- The two `BackgroundService` pumps in `OutboxHostedServices.cs` (`OutboxPollerService`,
  `OutboxWorkerService`) are deliberately thin: they own scope creation and the channel hop, and
  delegate the actual work to `OutboxPoller`/`OutboxWorkItemProcessor` above. `OutboxHostedServices.cs`
  holds the repo's only CA1031 exemption — see the file-scoped comment above
  `[src/Infrastructure/Outbox/OutboxHostedServices.cs]` in `.editorconfig` for why both pumps need
  it (a worker loop and a `BackgroundService`'s `ExecuteAsync` have no `IExceptionHandler`-style
  parameter to receive the exception, unlike the Api layer's global handler); root `CLAUDE.md`'s
  "Never `catch (Exception)`" bullet also describes it. Do not re-explain the reasoning here —
  point at `.editorconfig`, the one place it should live.

## dotnet-ef

`src/Infrastructure` is both the `--project` and the `--startup-project`: it has its own
`DesignTimeDbContextFactory`, so it needs nothing else to be self-sufficient at design time.
`src/Api` deliberately is **not** a valid startup project for this — giving it the
`Microsoft.EntityFrameworkCore.Design` package puts Roslyn in the production publish output
(measured: 7.9MB → 37MB), so don't add it there to make a startup-project error go away.

## EF rules

- **All mapping lives in `IEntityTypeConfiguration<T>`.** Never annotate a Domain type.
  Constraints, lengths, indexes, and required-ness are configured here.
- `AsNoTracking()` on every read that is not followed by a write.
- Lazy loading stays off. Load related data with explicit `Include`, or project to a DTO.
- Project with `Select` before materialising — never `ToListAsync()` then filter in memory.
- Never call `SaveChangesAsync` inside a loop.
- Migrations are append-only. Never hand-edit one; the protect-migrations hook blocks it.

## Tests

`tests/Infrastructure.Tests`. Repository behaviour against a real database
(Testcontainers) or the in-memory provider for pure mapping checks. Prefer the
real engine wherever query translation matters — the in-memory provider does not
reproduce it faithfully.
