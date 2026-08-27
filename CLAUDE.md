# AIFrameWork

.NET backend + Angular frontend, Clean Architecture, single repo.

## Versions

<!-- PLACEHOLDER: no .NET SDK and no Node were installed when this repo was set up.
     Pin real versions here the moment the toolchain is installed. Do not guess. -->

| | Version |
|---|---|
| .NET SDK | _unpinned_ |
| Angular | _unpinned_ |
| Node | _unpinned_ |

## Layout

| Path | Contents |
|---|---|
| `src/Domain` | Entities, value objects, domain events, domain exceptions |
| `src/Application` | Use cases, ports, `Result<T>`, validators |
| `src/Infrastructure` | EF Core, repositories, external clients |
| `src/Api` | Controllers, DTOs, exception handling, composition root |
| `frontend` | Angular workspace |
| `tests` | Test projects, one per layer |

## The dependency rule

Dependencies point inward. This is enforced by `.claude/hooks/dependency-rule.ps1`,
which blocks the edit rather than warning about it.

| From ↓ / To → | Domain | Application | Infrastructure | Api |
|---|---|---|---|---|
| **Domain** | — | ✗ | ✗ | ✗ |
| **Application** | ✓ | — | ✗ | ✗ |
| **Infrastructure** | ✓ | ✓ | — | ✗ |
| **Api** | ✓ | ✓ | ✓ DI only | — |

`Domain` additionally may not reference `Microsoft.EntityFrameworkCore`,
`Microsoft.AspNetCore`, `Microsoft.Extensions.DependencyInjection`, `System.Data`,
or `System.ComponentModel.DataAnnotations`.

## Non-negotiables

- **Warnings are errors.** All three sources — compiler, analyzers, build — see
  `Directory.Build.props`. Fix diagnostics; do not suppress them without a justification comment.
- **Nullable is enabled.** A missing null check does not compile.
- **`required` keyword in `Domain`, never `[Required]`.** DataAnnotations belong on `Api` DTOs.
- **`catch (Exception)` only in the global handler.** `throw;`, never `throw ex;`.
- **Never hand-edit an applied EF migration.** Add a new one.
- **No secrets in `appsettings*.json`.** Use `dotnet user-secrets` or environment variables.

## Commands

| Command | Does |
|---|---|
| `/feature <name>` | Scaffold a feature across all four layers, with tests |
| `/ng-feature <name>` | Scaffold an Angular feature |
| `/verify` | Build, test, and lint both stacks |
| `/adr <title>` | Record an architecture decision |

## More context

Each layer has its own `CLAUDE.md`, loaded when you work in that directory.
Conventions live in the `dotnet-conventions`, `dotnet-testing`, `angular-conventions`,
and `angular-testing` skills.

Design rationale: `docs/superpowers/specs/2026-08-27-claude-framework-design.md`
