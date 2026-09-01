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
