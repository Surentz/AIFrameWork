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
| `Api` DTOs | `required` + `init`, FluentValidation | business logic |
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

Hierarchy:

```csharp
public abstract class DomainException(string message) : Exception(message);
public sealed class NotFoundException(string message)   : DomainException(message);
public sealed class ConflictException(string message)   : DomainException(message);
public sealed class ValidationException(string message) : DomainException(message);
```

Analyzer-enforced as errors — CA1031 and CA2200, globally, with no per-file exemption:

- **Do not write `catch (Exception)` anywhere.** `IExceptionHandler.TryHandleAsync` receives the
  exception as a parameter, so the global handler needs no catch block of its own. If some other
  code genuinely requires one — a long-running background loop, say — it needs an explicit
  `#pragma warning disable CA1031` with a comment justifying it. CA1031 is `error` in
  `.editorconfig` with no scoping, so an unsuppressed one fails the build wherever it appears.
- `throw;` never `throw ex;` — the second erases the stack trace. CA2200.

Enforced by review, not by any analyzer:

- No empty catch blocks. No catch-log-continue that leaves the caller believing it succeeded.
  (SonarAnalyzer's S108 would cover this, but that package is commented out in
  `Directory.Build.props` until versions are pinned.)
- Expected failures return `Result<T>`; exceptions are for the genuinely exceptional. "Order
  not found" during a lookup is expected. A database being unreachable is not.

At the Api boundary, one `IExceptionHandler` maps the hierarchy to RFC 9457 `ProblemDetails`:
`ValidationException` → 400, `NotFoundException` → 404, `ConflictException` → 409, everything
else → 500, logged, message not leaked to the client.

## EF Core

- Mapping lives in `IEntityTypeConfiguration<T>`. Never annotate a Domain type.
- `AsNoTracking()` on every read not followed by a write.
- Lazy loading off. Explicit `Include`, or project to a DTO with `Select`.
- Project before materialising. Never `ToListAsync()` then filter in memory.
- Never `SaveChangesAsync` in a loop.
- Migrations are append-only. Add a new one; the protect-migrations hook blocks edits.

## Async

- `CancellationToken` on every I/O method, and pass it down.
- No `async void` outside event handlers. No `.Result`, no `.Wait()`.
- Do not add `ConfigureAwait(false)` — ASP.NET Core has no synchronization context, and
  CA2007 is deliberately off.
