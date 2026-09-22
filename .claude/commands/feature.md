---
description: Scaffold a backend feature across all four Clean Architecture layers, with tests
argument-hint: <FeatureName>
---

Implement the feature `$ARGUMENTS` across every layer, in dependency order.

Working outward in this order is what stops Clean Architecture degrading into one layer
edited and the other three forgotten. Do not skip ahead.

Read the `CLAUDE.md` of each layer as you reach it, and load
any skill the feature touches (`caching`, `auth`, `jobs`, `notifications`, `resilience`, `regenerate`).

## 1. Domain — `src/Domain/`

Entity or value object, invariants enforced in methods, private setters, `required`
properties. Add a domain exception if the feature has a failure mode the domain owns.
**No** DataAnnotations, no EF, no async.

## 2. Application — `src/Application/`

- Request and response models
- The port(s) this use case needs, if they do not exist yet — interfaces only
- The handler, returning `Result<T>`
- A FluentValidation validator for the request

Reference `Domain` only.

## 3. Infrastructure — `src/Infrastructure/`

- `IEntityTypeConfiguration<T>` for any new entity — all mapping, lengths, indexes here
- The port implementation
- **Do not hand-write a migration.** Tell the user the exact `dotnet ef migrations add`
  command to run.

## 4. Api — `src/Api/`

- Request/response DTOs, `required` + `init`, distinct from Domain types
- A thin controller action: bind, delegate, map `Result` to a status code
- DI registration if a new port was added

## 5. Tests

Write these as you go, not at the end:

| Project | Covers |
|---|---|
| `tests/Domain.Tests` | invariants, pure logic — no mocks |
| `tests/Application.Tests` | the handler, ports substituted with NSubstitute |
| `tests/Api.IntegrationTests` | the endpoint via `WebApplicationFactory` |

## 6. Finish

Run `/verify`. Then dispatch the `dotnet-reviewer` agent over the diff.

If any step is blocked — missing SDK, an unclear requirement — stop and say so rather than
inventing a shape for the layers below it.
