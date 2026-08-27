# Claude Code Framework — Design

**Date:** 2026-08-27
**Status:** Approved for planning
**Repo:** `AIFrameWork` — .NET backend + Angular frontend, monorepo

---

## 1. Context

The repository is empty: one commit containing `LICENSE` (Apache-2.0), plus an untracked
`.idea/` from Rider. There is no `.claude/`, no `.gitignore`, and no user-level `CLAUDE.md`.

The user runs Superpowers at user level (`effortLevel: high`, model `opus`) with Orca hooks bound
to most lifecycle events. Anything added at project level must **merge with** that configuration,
not replace it.

### Toolchain state, verified 2026-08-27

| Tool | Status |
|---|---|
| `dotnet` | Present at `C:\Program Files\dotnet\dotnet.exe` — **host 6.0.5, runtime only. "No SDKs were found."** |
| `node` / `npm` / `ng` | **Not found on PATH** |
| `git` | Present |

**Consequence:** no build, test, or lint command in this framework can be executed or verified on
this machine today. Every hook that shells out to `dotnet` or `npx` **must exit 0 silently when the
tool is absent.** A hook that errors on every edit until the SDK is installed gets disabled within a
day, taking the useful guardrails with it.

---

## 2. Goals

1. Encode Clean Architecture's dependency rule so it is **machine-checked**, not merely documented.
2. Make "no warnings" structural: warnings become build errors, and something forces a build.
3. Put the right conventions in front of Claude at the moment it opens a given layer.
4. Enforce null safety, `required`-ness, and disciplined exception handling on the backend.
5. Enforce Angular and TypeScript standards with a fast feedback loop.
6. Keep the surface small enough to maintain.

## 3. Non-goals

- Generating the .NET solution or Angular workspace — explicitly deferred.
- MCP servers, output styles, or a project-level skill library duplicating Superpowers.
- Auth, logging, deployment, CI pipeline design.

---

## 4. Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | Config only; no solution or workspace scaffold | User choice. Also unblocked by the missing SDK/Node. |
| D2 | Clean Architecture: `Domain` / `Application` / `Infrastructure` / `Api` | User choice. Fan-out risk mitigated by D3 and `/feature`. |
| D3 | Layered context: short root `CLAUDE.md` plus per-layer `CLAUDE.md` | Clean Architecture's failure mode is an agent holding `Api` conventions while editing `Domain`. Per-directory files load on demand and prevent that. |
| D4 | Guardrails that block, not advisory docs | User choice. Hooks block unambiguous violations only — never style. |
| D5 | Create layer directories containing only `CLAUDE.md` | User approved. Structure, not scaffold: no `.csproj`, no code. |
| D6 | Add `.gitignore`, `.editorconfig`, `Directory.Build.props` | User approved. Compile-time enforcement beats runtime hooks. |
| D7 | EF Core for persistence, engine left open | User confirmed EF. Nothing here depends on the engine. |
| D8 | `AnalysisMode=Recommended` + Sonar/Meziantou/AsyncFixer + curated elevations | User choice. `All` produces friction (CA1848, CA2007) that leads to suppression churn. |
| D9 | Stop hook enabled by default, one-line toggle | User asked twice for zero warnings; a gate is the only thing that delivers it. Costs nothing today — no-ops without the SDK. Reverses D4's "no strict gates"; flagged to the user and accepted. |
| D10 | Versions are placeholders, pinned at scaffold time | `dotnet --list-sdks` and `ng version` are unrunnable here. A written version would be a guess. |

### Assumptions

- Root namespace `AiFramework`, **configurable** in `.claude/hooks/hooks.config.json` — no hook hardcodes it.
- Test stack: xUnit + FluentAssertions + NSubstitute for .NET; Vitest for Angular, Karma being deprecated.
- Persistence reached through repository / `IUnitOfWork` interfaces in `Application`, with EF confined to
  `Infrastructure`. The alternative `IApplicationDbContext`-in-`Application` style would need one line of
  the dependency-rule hook relaxed.

---

## 5. Repository layout

```
CLAUDE.md
.gitignore
.editorconfig
Directory.Build.props
.prettierrc
.prettierignore
.claude/
  settings.json
  settings.local.json.example
  hooks/
    hooks.config.json
    lib/payload.ps1
    dependency-rule.ps1
    no-secrets.ps1
    protect-migrations.ps1
    format-and-lint.ps1
    verify-build.ps1
    tests/run-hook-tests.ps1
    tests/fixtures/*.json
  agents/
    dotnet-reviewer.md
    angular-reviewer.md
    test-runner.md
  commands/
    feature.md
    ng-feature.md
    verify.md
    adr.md
  skills/
    dotnet-conventions/SKILL.md
    dotnet-testing/SKILL.md
    angular-conventions/SKILL.md
    angular-testing/SKILL.md
src/Domain/CLAUDE.md
src/Application/CLAUDE.md
src/Infrastructure/CLAUDE.md
src/Api/CLAUDE.md
frontend/CLAUDE.md
frontend/eslint.config.js
tests/CLAUDE.md
docs/adr/0001-record-architecture-decisions.md
```

---

## 6. The rules

### 6.1 The dependency rule

Permitted references. Anything not ticked is a violation.

| From ↓ / To → | Domain | Application | Infrastructure | Api |
|---|---|---|---|---|
| **Domain** | — | ✗ | ✗ | ✗ |
| **Application** | ✓ | — | ✗ | ✗ |
| **Infrastructure** | ✓ | ✓ | — | ✗ |
| **Api** | ✓ | ✓ | ✓ (DI registration only) | — |

Banned `using` prefixes per layer, enforced by `dependency-rule.ps1`:

| Layer | Banned namespace prefixes |
|---|---|
| `Domain` | `{Root}.Application`, `{Root}.Infrastructure`, `{Root}.Api`, `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `Microsoft.Extensions.DependencyInjection`, `System.Data`, `System.ComponentModel.DataAnnotations` |
| `Application` | `{Root}.Infrastructure`, `{Root}.Api`, `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore` |
| `Infrastructure` | `{Root}.Api` |
| `Api` | none |

`System.ComponentModel.DataAnnotations` is banned in `Domain` specifically to prevent the annotation
trap in §6.3.

One row of the matrix is **documented but not enforced**: `Api` → `Infrastructure` is legitimate for
DI registration in composition root only, but a hook cannot distinguish a `services.AddScoped<...>()`
registration from a controller reaching into a repository directly. `Api` therefore has no banned
list, and this constraint is carried by `src/Api/CLAUDE.md` and `dotnet-reviewer` instead. Stating it
here so the plan does not attempt an unreliable check.

### 6.2 Null safety and required-ness

Enforced at compile time by `Directory.Build.props`:

```xml
<Nullable>enable</Nullable>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<CodeAnalysisTreatWarningsAsErrors>true</CodeAnalysisTreatWarningsAsErrors>
<MSBuildTreatWarningsAsErrors>true</MSBuildTreatWarningsAsErrors>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<EnableNETAnalyzers>true</EnableNETAnalyzers>
<AnalysisMode>Recommended</AnalysisMode>
<GenerateDocumentationFile>true</GenerateDocumentationFile>
<NoWarn>$(NoWarn);CS1591</NoWarn>
```

The three `TreatWarningsAsErrors` properties cover the three distinct warning sources — compiler
(`CS####`), analyzers (`CA####` / `S####`), and build plus restore (`MSB####` / `NU####`). Setting
only the common one leaves the other two able to emit ignorable warnings.

`CS8600`–`CS8655` therefore become **errors**: a missing null check cannot compile. `CS1591`
(missing XML docs) is suppressed — it fires on every public member and with warnings-as-errors would
stall development constantly. The documentation file stays enabled because Swagger consumes it.

Analyzer packages, declared once with `PrivateAssets="all"` so every future `.csproj` inherits them:
`SonarAnalyzer.CSharp`, `Meziantou.Analyzer`, `AsyncFixer`.

Curated elevations live in `.editorconfig` as explicit `dotnet_diagnostic.<ID>.severity = error`
entries. They are needed because `AnalysisMode=Recommended` leaves several of these rules disabled
rather than merely warning — `CodeAnalysisTreatWarningsAsErrors` cannot promote a rule that never
fires. The set: `CA1031` (do not catch general exception types), `CA2200` (rethrow to preserve
stack), `CA1062` (validate public arguments), `CA2000` / `CA1063` (disposal). `CA2007`
(`ConfigureAwait`) is explicitly set to `none` — it is noise in ASP.NET Core, which has no
synchronization context.

Coding rules, documented in `dotnet-conventions`, reviewed by `dotnet-reviewer`:

- Guard clauses use `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace`.
- No `!` null-forgiving operator without an adjacent comment justifying it.
- Collections are initialised, never null. Absence is `[]`, not `null`.

### 6.3 The annotation trap

This rule exists because "required annotations" collides with Clean Architecture in a way that is
easy to get backwards.

| Location | Use | Never use |
|---|---|---|
| `Domain` entities, value objects | C# `required` keyword, non-nullable types, private setters | `[Required]`, `[MaxLength]`, any `DataAnnotations` |
| `Api` DTOs and contracts | `required` + `init`, FluentValidation validators | business logic |
| `Infrastructure` | `IEntityTypeConfiguration<T>` for all mapping and constraints | annotations on domain types |

DataAnnotations drag persistence and presentation concerns into a layer that must stay ignorant of
both. The `Domain` half is enforced by the banned-namespace list in §6.1.

### 6.4 Exception handling

- A typed hierarchy: `DomainException` → `NotFoundException`, `ConflictException`, `ValidationException`.
- `IExceptionHandler` at the API boundary producing RFC 9457 `ProblemDetails`.
- `throw;` never `throw ex;` — CA2200, error.
- `catch (Exception)` banned everywhere except the single global handler — CA1031, error.
- `Result<T>` for expected failures; exceptions for genuinely exceptional conditions.
- No empty catch blocks. No catch-log-continue that hides a failure from the caller.

Frontend mirror: a global `ErrorHandler`, a typed HTTP error interceptor, and a lint rule against
swallowing errors in `catchError`.

### 6.5 Frontend standards

Compile-time and lint-time:

- `tsconfig`: `strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`, `noImplicitOverride`,
  `noFallthroughCasesInSwitch`, and `angularCompilerOptions.strictTemplates`.
  **Documented, not pre-written** — `ng new` generates this file and would overwrite anything placed first.
- `frontend/eslint.config.js`, flat config: `angular-eslint` recommended plus template a11y rules,
  `typescript-eslint` strict-type-checked, no `any`, component selector prefix, RxJS lifecycle rules.
- `ng lint --max-warnings 0`, so one lint warning is a non-zero exit.

Conventions, documented in `angular-conventions`:

- Standalone components only; `inject()` over constructor injection.
- Signals for local state; `ChangeDetectionStrategy.OnPush` everywhere.
- New control flow (`@if` / `@for`) over legacy structural directives.
- `takeUntilDestroyed` for subscription lifecycle; no manual `Subscription` bookkeeping.
- Typed reactive forms; no `FormGroup<any>`.

### 6.6 Secrets

`no-secrets.ps1` blocks writes to `appsettings*.json` containing a populated `Password=`, `Pwd=`,
`AccountKey=`, `SharedAccessSignature`, or a JSON key matching
`ApiKey|ClientSecret|Secret|Token|SigningKey|PrivateKey` with a non-placeholder value.

Allowed as placeholders: empty string, `${...}`, `#{...}`, `<...>`, `REPLACE_ME`, `CHANGEME`, `TODO`.
Real values belong in user-secrets or environment variables.

### 6.7 Migrations

`protect-migrations.ps1` blocks `Edit` / `MultiEdit` on `**/Migrations/*.cs`, including
`*.Designer.cs` and `*ModelSnapshot.cs`. Creating new migration files stays permitted. The block
message directs the user to generate a new migration rather than mutate an applied one.

---

## 7. Hook mechanics

All hooks are PowerShell 5.1, invoked `-NoProfile -ExecutionPolicy Bypass -File`. They read the
Claude Code JSON payload from stdin via the shared `lib/payload.ps1` helper.

**Contract every hook obeys:**

| Situation | Behaviour |
|---|---|
| Violation found | `exit 2` with an explanation on stderr, which Claude reads and acts on |
| Clean | `exit 0`, silent |
| Required tool missing (`dotnet`, `npx`) | `exit 0`, silent — never block on absent toolchain |
| Payload unparseable, or path outside the repo | `exit 0`, silent — fail open, never wedge the session |

| Hook | Event | Matcher | Blocks |
|---|---|---|---|
| `dependency-rule` | PreToolUse | `Edit`, `MultiEdit`, `Write` | layer violations per §6.1 |
| `no-secrets` | PreToolUse | `Edit`, `MultiEdit`, `Write` | secrets in `appsettings*.json` per §6.6 |
| `protect-migrations` | PreToolUse | `Edit`, `MultiEdit` | edits to existing migrations per §6.7 |
| `format-and-lint` | PostToolUse | `Edit`, `MultiEdit`, `Write` | nothing on `.cs`; lint errors on `.ts`/`.html` |
| `verify-build` | Stop | — | turn-end while the build is not warning-clean |

### 7.1 The feedback asymmetry

The two stacks cannot get the same loop, and the design does not pretend otherwise.

- **Frontend:** `eslint --fix` then `prettier --write` per file, roughly a second. Surviving lint
  errors `exit 2`, so they return to Claude and get fixed in the same turn.
- **Backend:** there is no fast per-file C# analyzer check — analyzers require a build. `.cs` files
  get `dotnet format whitespace` on write; warnings surface at build time through warnings-as-errors,
  the `verify-build` Stop hook, and `/verify`.

Net effect: TypeScript self-corrects immediately, C# at the next build.

### 7.2 Stop-hook loop safety

`verify-build.ps1` must read `stop_hook_active` from its payload and `exit 0` immediately when it is
true. Without that check, a build that cannot be made to pass would loop the session indefinitely.
It also exits 0 when no `.sln` or `.csproj` exists, which is the state today.

---

## 8. `settings.json`

**Permissions.** Allow the loop actually used, so the same approvals stop recurring:
`dotnet build|test|restore|format|ef|new|sln`, `npm ci`, `npm run *`, `npx ng *`, and read-only git
(`status`, `diff`, `log`, `branch`).

Deny outright: reads of `**/.env`, `**/secrets.json`, `**/*.pfx`; and `dotnet ef database drop`.
Ask: `git push`.

**Hooks.** The five above, wired by event. Project hooks merge with the user-level Orca hooks rather
than replacing them.

`settings.local.json` is gitignored for personal overrides; a committed
`settings.local.json.example` documents the shape, including how to disable `verify-build` (D9).

---

## 9. Context layer

`CLAUDE.md` at the root stays roughly a page: stack and pinned versions (placeholders per D10), the
directory map, the dependency rule stated once as law, the command table, and pointers to the
per-layer files. Long root files get skimmed; this one is meant to be read every time.

Each per-layer file is 10–25 lines answering only three questions — what belongs here, what must
never be referenced from here, what a test at this layer looks like.

| File | Core content |
|---|---|
| `src/Domain/CLAUDE.md` | Entities, value objects, domain events, domain exceptions. No EF, no ASP.NET, no DI, no DataAnnotations, no `async`. Pure unit tests, no mocks. |
| `src/Application/CLAUDE.md` | Use cases, ports (repository / `IUnitOfWork` interfaces), `Result<T>`, validators. References `Domain` only. Tests mock ports with NSubstitute. |
| `src/Infrastructure/CLAUDE.md` | EF `DbContext`, `IEntityTypeConfiguration<T>`, repository implementations, external clients. `AsNoTracking` for reads, explicit `Include`, no lazy loading. |
| `src/Api/CLAUDE.md` | Thin controllers, DTOs with `required` + `init`, FluentValidation, `IExceptionHandler` + `ProblemDetails`. No business logic. Integration tests via `WebApplicationFactory`. |
| `frontend/CLAUDE.md` | §6.5 conventions, the `tsconfig` delta to apply after `ng new`, lint and format commands. |
| `tests/CLAUDE.md` | Test project layout, naming, the xUnit/FluentAssertions/NSubstitute stack, what belongs at each level. |

---

## 10. Agents

Three, each earning its place by keeping noise out of the main context.

| Agent | Purpose |
|---|---|
| `dotnet-reviewer` | Dependency rule, nullability, the §6.3 annotation trap, exception discipline per §6.4, EF pitfalls (N+1, unintended tracking, missing `AsNoTracking`), async correctness. |
| `angular-reviewer` | Standalone components, signals, OnPush, subscription leaks, typed forms, template a11y, error swallowing. |
| `test-runner` | Runs both suites and reports parsed failures, instead of dumping build logs into the conversation. |

---

## 11. Commands

| Command | Purpose |
|---|---|
| `/feature <name>` | Scaffolds one feature across all four layers in dependency order, with tests alongside. The coherent fan-out that stops Clean Architecture degrading into an agent editing one layer and forgetting the rest. |
| `/ng-feature <name>` | Angular feature: routed standalone component, service, typed model, spec files. |
| `/verify` | Build, test, and lint both stacks; report warnings-as-errors failures. |
| `/adr <title>` | Writes an ADR into `docs/adr/` using the 0001 template. |

---

## 12. Skills

| Skill | Contents |
|---|---|
| `dotnet-conventions` | Nullability and `required` (§6.2), the annotation trap (§6.3), exception hierarchy and `ProblemDetails` (§6.4), guard clauses, EF query and configuration patterns. |
| `dotnet-testing` | xUnit + FluentAssertions + NSubstitute, naming, what to test at each layer, `WebApplicationFactory` integration tests, no mocking of types you do not own. |
| `angular-conventions` | Standalone, signals, `inject()`, OnPush, new control flow, `takeUntilDestroyed`, typed forms, HTTP error handling (§6.5). |
| `angular-testing` | Vitest setup, component testing, `HttpClient` mocking, what not to test. |

Each is a project skill under `.claude/skills/`. None duplicates Superpowers: these describe **what
good code looks like in this repo**, whereas Superpowers describes **how to work**. Two sources of
truth on process would be worse than one.

---

## 13. Verification

The hooks are the only executable part of this framework, and they are verifiable today without the
SDK or Node.

`.claude/hooks/tests/run-hook-tests.ps1` feeds each hook the JSON payload shape Claude Code actually
sends, from fixtures, and asserts exit codes:

| Case | Expected |
|---|---|
| `Domain` file with `using Microsoft.EntityFrameworkCore;` | exit 2 |
| `Domain` file with `using System;` | exit 0 |
| `Application` file referencing `{Root}.Infrastructure` | exit 2 |
| `Api` file referencing all layers | exit 0 |
| `appsettings.json` with `Password=hunter2` | exit 2 |
| `appsettings.json` with `Password=${DB_PASSWORD}` | exit 0 |
| Edit to an existing `Migrations/*.cs` | exit 2 |
| `format-and-lint` with `dotnet`/`npx` absent | exit 0, silent |
| `verify-build` with `stop_hook_active: true` | exit 0 |
| Malformed payload | exit 0 |

Everything else in the framework is text and will be **unexecuted until the toolchain is installed
and the solution scaffolded.** The plan must not claim otherwise.

---

## 14. Risks and open items

| Risk | Mitigation |
|---|---|
| Hooks are unrunnable against real builds today | Fixture tests cover hook logic; toolchain-absent paths explicitly tested |
| `verify-build` friction once the SDK lands | One-line toggle, documented in `settings.local.json.example` (D9) |
| Per-layer `CLAUDE.md` files are speculative until code sits beside them | Kept to 10–25 lines; revisit after the first real feature |
| Version placeholders could go stale or be forgotten | Marked explicitly in root `CLAUDE.md` as fill-at-scaffold |
| `ng new` overwriting pre-written frontend config | Only `eslint.config.js` and `.prettierrc` are pre-written; `tsconfig` changes are documented as a post-`ng new` step |
| Root namespace assumed `AiFramework` | Read from `hooks.config.json`; single-line change |

### Deferred to a later pass

1. Install .NET SDK and Node; pin real versions into root `CLAUDE.md`.
2. Scaffold the solution and Angular workspace.
3. Apply the documented `tsconfig` delta.
4. Run `run-hook-tests.ps1` against real files, and `/verify` end to end.
