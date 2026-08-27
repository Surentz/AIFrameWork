# Infrastructure

Implements the ports that Application declares. This is the only layer that knows EF exists.

## Belongs here

`DbContext`, `IEntityTypeConfiguration<T>` classes, repository implementations,
migrations, HTTP clients for external services, and the DI registration extension
this layer exposes to Api.

## Never appears here

Any `AiFramework.Api` namespace. Blocked by the dependency-rule hook.

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
