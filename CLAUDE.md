# AIFrameWork

.NET backend + Angular frontend, Clean Architecture, single repo.

## Versions

| | Version |
|---|---|
| .NET SDK | 10.0.400 |
| Target framework | net10.0 |
| Node | 24.20.0 |
| Angular | _unpinned — set at `ng new`_ |

Verified 2026-08-28 from `dotnet --list-sdks` and `node --version`. The target framework was
pinned when the solution was scaffolded. Angular stays unpinned until the workspace is
created; pin it from the generated `package.json`, not from memory.

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

> The `Api → Infrastructure` cell is the one row the hook does **not** enforce: nothing
> distinguishes a `services.AddScoped<>()` registration from a controller reaching into a
> repository, so "DI only" is carried by review and `dotnet-reviewer`. Every other cell blocks.

`Domain` additionally may not reference `Microsoft.EntityFrameworkCore`,
`Microsoft.AspNetCore`, `Microsoft.Extensions.DependencyInjection`, `System.Data`,
or `System.ComponentModel.DataAnnotations`.

## Non-negotiables

- **Warnings are errors.** All three sources — compiler, analyzers, build — see
  `Directory.Build.props`. Fix diagnostics; do not suppress them without a justification comment.
- **Nullable is enabled.** A missing null check does not compile.
- **`required` keyword in `Domain`, never `[Required]`.** DataAnnotations belong on `Api` DTOs.
- **Never `catch (Exception)`.** The global `IExceptionHandler` receives it as a parameter.
  CA1031 is a global error with no exemption. `throw;`, never `throw ex;`.
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
