# AIFrameWork

.NET backend + React frontend, Clean Architecture, single repo.

This file holds what applies to every change. Detail lives in the per-layer `CLAUDE.md` files
(loaded when you work in that directory) and in the skills listed under
[Commands and skills](#commands-and-skills) — load the matching skill before working in its area.
Rationale lives in `docs/adr/`.

## Versions

| | Version | Where it is pinned |
|---|---|---|
| .NET SDK | 10.0.400 | `.github/workflows/ci.yml` and `e2e.yml`. There is no `global.json`, so any 10.x SDK builds |
| Target framework | net10.0 | `Directory.Build.props`, alongside `LangVersion` 14.0 |
| Node | 24.20.0 | `.github/workflows/ci.yml` and `e2e.yml`. 24.15.0 is the hard floor — see below |
| React | 19.3.0 | `frontend/package-lock.json`, declared `^19.2.8` |
| Vite | 8.3.0 | `frontend/package-lock.json`, declared `^8.2.2` |
| TypeScript | 6.0.3 | `frontend/package-lock.json`, declared `~6.0.2` |

Read resolved versions from the *lockfile*, not `package.json` — the declared ranges are carets.
CI is the authority for the SDK and Node, because nothing in the repo pins either. Before
treating a green local build as evidence about CI, check `dotnet --list-sdks` and
`node --version` against the table.

**Node 24.15.0 is a floor, not a preference.** Below it `npm install` in `frontend/` fails
outright (npm's own `engines`, jsdom, and a resolver crash:
`Cannot read properties of null (reading 'edgesOut')`). `scripts/install-prereqs.ps1` checks it.

## Layout

| Path | Contents |
|---|---|
| `src/Domain` | Entities, value objects, domain events, domain exceptions |
| `src/Application` | Use cases, ports, `Result<T>`, validators |
| `src/Infrastructure` | EF Core, repositories, external clients |
| `src/Api` | Controllers, DTOs, exception handling, composition root |
| `src/Worker` | The job host: composition, health endpoints, its own generated adapters |
| `frontend` | Vite + React workspace (`frontend/e2e`: Playwright) |
| `tests` | Test projects, one per layer |
| `k8s`, `deploy` | Kustomize manifests and the kind-cluster scripts |
| `docs/adr` | Architecture decisions — the "why" behind every rule here |

## The dependency rule

Dependencies point inward. `.claude/hooks/dependency-rule.ps1` blocks the edit rather than
warning about it.

| From ↓ / To → | Domain | Application | Infrastructure | Api | Worker |
|---|---|---|---|---|---|
| **Domain** | — | ✗ | ✗ | ✗ | ✗ |
| **Application** | ✓ | — | ✗ | ✗ | ✗ |
| **Infrastructure** | ✓ | ✓ | — | ✗ | ✗ |
| **Api** | ✓ | ✓ | ✓ DI only | — | ✗ |
| **Worker** | ✓ | ✓ | ✓ DI only | ✗ | — |

What the hook does **not** catch, so review and `dotnet-reviewer` must:

- **"DI only"** — a controller reaching into a repository looks the same to the hook as a
  `services.AddScoped<>()` registration.
- **`Domain`/`Application`/`Infrastructure → Worker`** — the hook's `$banned` table lists
  `Worker` only under `Api`. The fix is adding `"$root.Worker"` to those three lists; nothing
  violates them today.
- **Fully-qualified inline references** with no `using`, and **`<ProjectReference>`** in a
  `.csproj` (the hook gates on `\.cs$`).

**`Api` and `Worker` are sibling composition roots, not layers** (ADR 0016). Neither may
reference the other — `Worker → Api` is exactly what would let them share one Wolverine
generated-code tree, and keeping them apart is what makes each host's `codegen write` correct.

`Domain` additionally may not reference `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`,
`Microsoft.Extensions.DependencyInjection`, `System.Data`, or
`System.ComponentModel.DataAnnotations`.

## Non-negotiables

- **Warnings are errors** — compiler, analyzers and build (`Directory.Build.props`). Fix
  diagnostics. Repo-wide exemptions live in `.editorconfig` as `severity = none`; anything
  narrower is a local `#pragma warning disable`/`restore` pair with a justification comment right
  above it (e.g. `src/Application/Abstractions/Messaging.cs`).
- **Nullable is enabled.** A missing null check does not compile.
- **`required` keyword in `Domain`, never `[Required]`.** DataAnnotations belong on `Api` DTOs.
- **Never `catch (Exception)`.** The global `IExceptionHandler` receives it as a parameter.
  CA1031 is an error everywhere except the outbox pumps' file-scoped exemption in
  `.editorconfig`. `throw;`, never `throw ex;`.
- **Never hand-edit an applied EF migration.** Add a new one. `.claude/hooks/protect-migrations.ps1`
  keys on git history: an uncommitted migration you just generated is still yours to adjust; one
  in `HEAD` is refused even if you delete the file first.
- **No secrets in `appsettings*.json`.** Use `dotnet user-secrets` or environment variables. The
  committed dev connection string and the k8s overlay's `secret.yaml`/`tls.yaml` are deliberate
  throwaway localhost values, not a pattern to copy.
- **Config keys from the environment use double underscores** — `Cache__Enabled`, never
  `Cache_Enabled`. A single underscore binds nothing and warns nothing.

## Commands and skills

| Command | Does |
|---|---|
| `/feature <name>` | Scaffold a feature across all four layers, with tests |
| `/react-feature <name>` | Scaffold a React feature |
| `/verify` | Build, test, and lint both stacks, plus the codegen and contract diffs |
| `/adr <title>` | Record an architecture decision |
| `/job <name>` | Add a background job: message, handler, registration, tests, regenerated adapters |

| Skill | Load it when |
|---|---|
| `dotnet-conventions` / `dotnet-testing` | Writing C# / .NET tests |
| `react-conventions` / `react-testing` | Writing React / React tests |
| `regenerate` | After changing a controller, DTO, `[ProducesResponseType]`, or any Wolverine/job handler; a red `codegen` or `contract` CI job |
| `jobs` | Editing or debugging a background job, Quartz schedule, retry or dead-lettering |
| `caching` | Making a query cacheable, adding eviction, stale or cross-user data |
| `auth` | Sign-in, sessions, the security stamp, the `Admin` role, the sign-in audit |
| `notifications` | The notification feed, handlers that write to it, SignalR push |
| `observability` | Logging, tracing, OTLP, traffic metrics and their charts |
| `resilience` | Outbound HTTP clients, retry/timeouts, explicit DB transactions |
| `local-dev` | The dev loop scripts, Seq, ports, `dotnet ef` vs user-secrets, the control panel |
| `kubernetes` | The kind cluster, `deploy.ps1`, HPAs, ingress affinity, `-WithObservability` |

Agents: `dotnet-reviewer` and `react-reviewer` before committing; `test-runner` for test
results without logs in the conversation.

## Running locally

```powershell
./scripts/dev.ps1            # Postgres + API (5234) + job worker (5235) + Vite (5173)
./scripts/worker.ps1         # restart just the worker — required after `codegen write`
./scripts/stop-dev.ps1
```

Dev Postgres is **55433** (persistent); the e2e one is **55432** (throwaway). `npm run e2e`
starts its own API on 5234 and worker on 5235 (ADR 0023), so stop the dev loop first or set
`API_PORT` / `WORKER_PORT`.

**`dotnet ef` cannot see user-secrets** — it reads only the `ConnectionStrings__Default`
environment variable, and `src/Infrastructure` is both `--project` and `--startup-project`
(never `src/Api`: see `src/Infrastructure/CLAUDE.md`). Conversely a stale user-secret silently
overrides `appsettings.Development.json` at runtime. The `local-dev` skill has the commands;
`docs/local-development.md` covers IDE startup.

## Generated artifacts are committed

Three generated artifacts are committed and CI fails on any **diff** — a tree that builds and
tests clean can still be rejected. Use the `regenerate` skill for the exact commands.

- **Wolverine adapters — two trees**, `src/Api/Internal/Generated` and
  `src/Worker/Internal/Generated`. Debug compiles adapters at startup, so a stale tree stays green
  in Debug and **breaks only in Release, at startup**. Regenerate with
  `dotnet run --project src/{Api,Worker} -- codegen write`. CI's Linux output wins over Windows.
- **The API contract** — `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts`, after
  any controller, DTO or `[ProducesResponseType]` change. It needs placeholder env vars
  (`ConnectionStrings__Default`, `Wolverine__Durable=false`, `Admin__ReconcileOnStart=false`);
  **any new startup path that dials Postgres needs its own switch** there too.

## Rules that fail silently

Each of these breaks with no compiler error and often no failing test. The linked skill has the
full reasoning.

**Jobs** (`jobs`, ADR 0016) — Jobs run in the worker; **the API listens to no queue**. An
`EnqueueAsync` is **not transactional** with the command: a job that must not be lost is
enqueued from a domain event handler. A job touching user data implements `IUserScopedJob`.
`ICurrentUser` and `IClientContext` are registered **per host**, never in `AddInfrastructure`.
Never start a Quartz scheduler in the API.

**Caching** (`caching`, ADR 0009) — `ICacheable.CacheKey` **never contains a user id**; the
behavior scopes it. Nothing on the auth path, and not the notification feed, is ever
`ICacheable`. The cache stores `TResponse`, never `Result<T>`. It is off under test.

**Auth** (`auth`, ADRs 0011/0020/0022) — Every authenticated request reads the security stamp
and role from the database, uncached. Rotating `User.SecurityStamp` signs out every session; a
**role change must not rotate it**. `Admin__Usernames` only ever promotes, so removing an admin
takes two steps. `sign_in_events` is personal data with a 30-day prune.

**Logging** (`observability`, ADR 0015) — `Behaviors.LoggedAsync` already logs every command
and query: **no hand-written "handling X" logging**, and **never log a request instance** (three
commands carry plaintext passwords).

**Outbox handlers** (`notifications`, `src/Application/CLAUDE.md`) — Delivery is at-least-once,
so handlers are idempotent, and a domain event handler runs outside the command pipeline, so it
**must call `SaveChangesAsync` itself**.

**Resilience** (`resilience`, ADR 0014) — Polly cannot see a failed `Result<T>`: the pipeline
sits under the port, never around it. With `EnableRetryOnFailure` on, an explicit transaction
must go through `CreateExecutionStrategy().ExecuteAsync(...)`.

## CI

`.github/workflows/ci.yml` runs on every push to `main` and every PR, in five jobs: `backend`
(**Debug and Release** — Release was broken for the whole Wolverine spike behind a green Debug
build, because only its *startup* failed), `codegen`,
`contract`, `frontend`, and `e2e`. `/verify` runs the same checks locally, including the two
diff checks. The `e2e` job is defined in `.github/workflows/e2e.yml`, which `ci.yml` calls; the
same workflow runs by hand from the Actions tab, with optional `grep` and `repeat_each` inputs.

`main` is protected by a branch ruleset that requires all six checks **by name**: `backend (Debug)`,
`backend (Release)`, `generated code is current`, `api contract is current`, `frontend` and
`e2e / e2e`. Renaming a job, or adding a path filter to the `pull_request` trigger, leaves a
required check that never reports, and every PR blocks — update the ruleset in the same change.

Design rationale for this setup: `docs/superpowers/specs/2026-08-27-claude-framework-design.md`
