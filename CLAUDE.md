# AIFrameWork

.NET backend + React frontend, Clean Architecture, single repo.

## Versions

| | Version |
|---|---|
| .NET SDK | 10.0.400 |
| Target framework | net10.0 |
| Node | 24.20.0 |
| React | 19.2.8 |
| Vite | 8.2.2 |

Verified 2026-09-03 from `dotnet --list-sdks`, `node --version`, and `frontend/package.json`
(post-install, resolved versions). The target framework was pinned when the solution was
scaffolded. React and Vite were pinned once the workspace was created, read from the
generated `package.json` — not from memory.

## Layout

| Path | Contents |
|---|---|
| `src/Domain` | Entities, value objects, domain events, domain exceptions |
| `src/Application` | Use cases, ports, `Result<T>`, validators |
| `src/Infrastructure` | EF Core, repositories, external clients |
| `src/Api` | Controllers, DTOs, exception handling, composition root |
| `frontend` | Vite + React workspace |
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
  Repo-wide exemptions live in `.editorconfig` as `severity = none` rules — that file is the
  place to look for what's off and why. Everything narrower is a local `#pragma warning
  disable`/`restore` pair at the point of use, and every one of those carries a justification
  comment right above it explaining why the rule's intent doesn't apply there (see, for example,
  `src/Application/Abstractions/Messaging.cs` and `tests/Infrastructure.Tests/Persistence/
  PostgresFixture.cs`). Grep for `#pragma warning disable` if you need the current, exact list —
  it grows as new sites are justified, so no count is kept here.
- **Nullable is enabled.** A missing null check does not compile.
- **`required` keyword in `Domain`, never `[Required]`.** DataAnnotations belong on `Api` DTOs.
- **Never `catch (Exception)`.** The global `IExceptionHandler` receives it as a parameter.
  CA1031 is an error everywhere except one file-scoped exemption in `.editorconfig`, for the
  outbox's `BackgroundService` pumps, which have no such parameter and must not die mid-loop.
  `throw;`, never `throw ex;`.
- **Never hand-edit an applied EF migration.** Add a new one.
- **No secrets in `appsettings*.json`.** Use `dotnet user-secrets` or environment variables.

## Commands

| Command | Does |
|---|---|
| `/feature <name>` | Scaffold a feature across all four layers, with tests |
| `/react-feature <name>` | Scaffold a React feature |
| `/verify` | Build, test, and lint both stacks |
| `/adr <title>` | Record an architecture decision |

## Running locally

```bash
docker compose up -d --wait                                  # dev Postgres on 55433
dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
dotnet run --project src/Api                                 # then `npm start` in frontend/
```

Two databases, two ports, and they are meant to coexist: **55433** is the dev database from
`docker-compose.yml` (named volume, data persists); **55432** is the e2e one from
`docker-compose.e2e.yml` (throwaway). Override either with `DEV_PG_PORT` / `PG_PORT`.

The dev connection string is committed in `src/Api/appsettings.Development.json` — throwaway
credentials against a localhost-only container that is never deployed, the same judgement
already applied to `docker-compose.e2e.yml`. The no-secrets-in-`appsettings*.json` rule still
holds for everything else.

The two *databases* coexist happily, but the two *API* processes do not: `npm run e2e` starts
its own API on 5234 — the same port `dotnet run` uses — with `reuseExistingServer: false`. Stop
the dev API before an e2e run, or set `API_PORT`.

**The gotcha that will cost you an afternoon: `dotnet ef` cannot see user-secrets.** Migrations
run through `src/Infrastructure/Persistence/DesignTimeDbContextFactory.cs`, which reads only the
`ConnectionStrings__Default` environment variable — so `database update` against anything but
the default needs it passed explicitly:

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
```

The reverse also bites: a user-secret sits *above* `appsettings.Development.json` in the
configuration order, so a stale `ConnectionStrings:Default` there silently wins over the
committed value at runtime while `dotnet ef` ignores it. Check with
`dotnet user-secrets list --project src/Api` if the app and the migrations disagree about which
database they are talking to.

## Wolverine codegen

Wolverine builds its handler adapters with Roslyn, and **Release ships without the compiler** —
it costs 33MB (measured: a Release publish is 17MB without it, 50MB with). Release instead loads
adapters generated ahead of time and committed under `src/Api/Internal/Generated`.

**After adding or changing a Wolverine handler, regenerate them:**

```bash
dotnet run --project src/Api -- codegen write
```

Then commit the result. Debug does not need this — it still compiles adapters at startup — which
is exactly the trap: stale generated code leaves Debug green and the build succeeding, and breaks
only in Release, at startup. `WolverineCodegenTests` exists to catch that in the Debug suite; if
it fails, the fix is the command above.

`Program.cs` therefore ends in `RunJasperFxCommands(args)` rather than `RunAsync()`, which is what
makes `codegen write` reachable. Ordinary `dotnet run` is unaffected. Tests using
`WebApplicationFactory` need `JasperFxEnvironment.AutoStartHost` — set once for the whole test
assembly in `tests/Api.IntegrationTests/JasperFxTestEnvironment.cs`; without it every one of them
fails with "The server has not been started".

See ADR 0005.

## More context

Each layer has its own `CLAUDE.md`, loaded when you work in that directory.
Conventions live in the `dotnet-conventions`, `dotnet-testing`, `react-conventions`,
and `react-testing` skills.

Design rationale: `docs/superpowers/specs/2026-08-27-claude-framework-design.md`
