---
name: dotnet-conventions
description: Use when writing or modifying C# in this repo - covers nullability, required-ness, the DataAnnotations trap, exception handling, and EF Core patterns.
---

# .NET Conventions

Warnings are errors here (`Directory.Build.props`). Most of this is enforced by the compiler;
the rest is enforced by review.

## Nullability

Nullable reference types are on and every nullable warning is a build error.

- A parameter that may be null is typed `T?`. One that may not is typed `T` — and callers
  are then free to rely on that.
- `!` requires an adjacent comment stating why null is impossible. No comment, no `!`.
- Guard public entry points: `ArgumentNullException.ThrowIfNull(order);`
  `ArgumentException.ThrowIfNullOrWhiteSpace(name);`
- Collections are initialised, never null. Absence is `[]`.

## Required-ness — and where annotations belong

This is the easiest rule in the repo to get backwards.

| Layer | Use | Never |
|---|---|---|
| `Domain` | C# `required` keyword, non-nullable types, private setters | `[Required]`, `[MaxLength]`, any DataAnnotations |
| `Api` DTOs | `required` + `init`; DataAnnotations only for HTTP shape | business logic, a rule a validator owns |
| `Application` commands | a FluentValidation validator, registered in `AddMessaging()` | — |
| `Infrastructure` | `IEntityTypeConfiguration<T>` for constraints | annotations on Domain types |

```csharp
// Domain - correct
public sealed class Order
{
    public required OrderId Id { get; init; }
    public required CustomerId CustomerId { get; init; }
    private readonly List<OrderLine> _lines = [];
    public IReadOnlyCollection<OrderLine> Lines => _lines.AsReadOnly();
}

// Domain - wrong: DataAnnotations drag persistence and validation into the core
public sealed class Order
{
    [Required, MaxLength(50)]
    public string Reference { get; set; } = string.Empty;
}
```

The dependency-rule hook blocks `System.ComponentModel.DataAnnotations` in `Domain`.

## Exception handling

**Not-found, conflict and validation are not exceptions here** — they are a failed `Result<T>`
with an `ErrorKind`. Exceptions are only for a broken domain invariant:

```csharp
public class DomainException : Exception;              // src/Domain — any broken invariant → 400
public class OrderStateException : DomainException;    // an illegal transition (ship twice, cancel shipped)
```

A subtype exists only when one call can fail two ways that need different statuses:
`Order.Cancel` throws `OrderStateException` for "already shipped" and a plain `DomainException` for
a bad reason, and `CancelOrderHandler` catches the former and returns `ErrorKind.Conflict` (409).
Do not add `NotFoundException`/`ConflictException`; return the `Result`.

Analyzer-enforced as errors — CA1031 and CA2200:

- **Do not write `catch (Exception)` anywhere.** `IExceptionHandler.TryHandleAsync` receives the
  exception as a parameter, so the global handler needs no catch block of its own. If some other
  code genuinely requires one — a long-running background loop, say — it needs an explicit
  `#pragma warning disable CA1031` with a comment justifying it (`NotificationHub` has one).
  The only file-scoped exemption is `src/Infrastructure/Outbox/OutboxHostedServices.cs` in
  `.editorconfig`, reasoned there; do not add another.
- `throw;` never `throw ex;` — the second erases the stack trace. CA2200.

Also analyzer-enforced, via SonarAnalyzer (active since 2026-08-28):

- No empty catch blocks — `S108`, and `S2486` ("handle the exception or explain in a comment
  why it can be ignored"). Both verified firing as errors.

Enforced by review, not by any analyzer:

- No catch-log-continue that leaves the caller believing it succeeded. An analyzer cannot tell
  a swallowed failure from a deliberately handled one.
- Expected failures return `Result<T>`; exceptions are for the genuinely exceptional. "Order
  not found" during a lookup is expected. A database being unreachable is not. No Roslyn rule
  can judge which is which.

At the Api boundary, two paths produce RFC 9457 `ProblemDetails` (`src/Api/CLAUDE.md` has both
tables). `ResultExtensions.Problem` maps a failed `Result`'s `ErrorKind`: `Validation` → 400,
`Unauthorized` → 401, `NotFound` → 404, `Conflict` → 409, `Unavailable` → 503 with `Retry-After`.
`GlobalExceptionHandler` maps a thrown `DomainException` → 400, and everything else → 500,
logged, message not leaked to the client.

## EF Core

- Mapping lives in `IEntityTypeConfiguration<T>`. Never annotate a Domain type.
- `AsNoTracking()` on every read not followed by a write.
- Lazy loading off. Explicit `Include`, or project to a DTO with `Select`.
- Project before materialising. Never `ToListAsync()` then filter in memory.
- Never `SaveChangesAsync` in a loop.
- Migrations are append-only. Add a new one; the protect-migrations hook blocks edits.

## Caching

Two rules, and both prevent a bug rather than tidy anything.

- **`ICacheable.CacheKey` never contains a user id.** The caching behavior prepends the query type
  name and `ICurrentUser.Id` itself, so a key that repeats the caller duplicates it rather than
  securing it — and a key the author *forgot* to scope is impossible, which is the point of
  composing it in the behavior. `CacheKey` is the query's own arguments, nothing else.
- **A query is cached only when it opts in, and nothing on the auth path ever opts in.** `GetUser`
  is the query to leave alone, and any future one that reads account state: ADR 0008's lockout and
  failed-attempt counters must come from the database every time, so serving them from memory is a
  security bug rather than a stale read. (`SignIn`, `RegisterUser`, and `ChangePassword` are
  commands and so are not cacheable at all — the caching behavior is on the query path only, and
  `EveryCacheableType_IsARegisteredQuery` fails on an `ICacheable` that is not a query.)

Commands opt into eviction with `IInvalidatesCache`, whose `Tags` name query types via
`nameof(TheQuery)` — never a string literal. See ADR 0009 for the rest.

## Async

- `CancellationToken` on every I/O method, and pass it down.
- No `async void` outside event handlers. No `.Result`, no `.Wait()`.
- `ConfigureAwait(false)` is not enforced in either direction — CA2007 is deliberately off, and
  ASP.NET Core has no synchronization context, so it changes nothing here. Existing code under
  `src/` uses it throughout (including the messaging behaviors); match the file you are working
  in rather than either adding or stripping it.
