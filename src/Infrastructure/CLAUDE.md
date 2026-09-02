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
- Only commands get behaviors — validation, then unit-of-work commit. Queries get neither.
  A query that needs a transaction is a command.

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
