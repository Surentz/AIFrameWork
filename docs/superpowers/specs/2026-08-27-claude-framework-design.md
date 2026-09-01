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
| D8 | `AnalysisMode=Recommended` + Sonar/Meziantou/AsyncFixer + curated elevations | User choice. `All` produces friction (CA1848, CA2007) that leads to suppression churn. The three analyzer packages went live 2026-08-28 with pinned versions — see §6.2. |
| D9 | Stop hook enabled by default; disabled by deleting the `Stop` block from the shared `settings.json`, or all-or-nothing via local `disableAllHooks` | User asked twice for zero warnings; a gate is the only thing that delivers it. Costs nothing today — no-ops without the SDK. Reverses D4's "no strict gates"; flagged to the user and accepted. |
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

The hook identifies a layer from the file path. It matches both the bare `src/Domain/` layout and the
root-namespace-prefixed `src/AiFramework.Domain/` layout that `dotnet new classlib -o src/<Root>.<Layer>`
produces, requiring the layer name to be a complete path segment so `src/DomainServices/` is not
mistaken for `Domain`. The layer names themselves come from the `layers` array in
`.claude/hooks/hooks.config.json`, falling back to the canonical four if that file is missing or
unreadable. Renaming a layer folder to something the config does not list disables the rule for it,
which is why `src/Domain/CLAUDE.md` says the folder name is load-bearing.

Two things are **documented but not enforced**, both carried by `src/Api/CLAUDE.md`,
`docs/adr/0002-*.md` and `dotnet-reviewer`. Stated here so the plan does not attempt unreliable
checks:

1. `Api` → `Infrastructure` is legitimate for DI registration in the composition root only, but a
   hook cannot distinguish a `services.AddScoped<...>()` registration from a controller reaching
   into a repository directly. `Api` therefore has no banned list.
2. **`.csproj` files.** The hook gates on `\.cs$`, so project files never reach it. A
   `<ProjectReference>` from `Domain.csproj` to `Infrastructure.csproj` — the coarsest possible
   violation, and the one that makes every `using` beneath it legal — is entirely invisible. This
   is a real gap, not a rounding error, and it is why the rule is described as hard to rot rather
   than impossible to rot.

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

Analyzer packages, declared once with `PrivateAssets="all"` so every future `.csproj` inherits
them: `SonarAnalyzer.CSharp` 10.33.0.1635, `Meziantou.Analyzer` 3.0.190, `AsyncFixer` 2.1.0.

**Active since 2026-08-28**, when .NET SDK 10.0.400 was installed and versions were pinned from
nuget.org. Verified end to end against a throwaway project: clean code builds with 0 warnings and
0 errors; a `return maybe;` on a `string?` fails with `CS8603`; a `catch (Exception) { }` fails
with `CA1031`, `S2486` and `S108` — confirming both the `.editorconfig` elevations and the Sonar
package are genuinely live, not merely configured. See D8.

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
- `catch (Exception)` banned outright — CA1031 is `error` globally with no per-file exemption.
  `IExceptionHandler` receives the exception as a parameter and needs no catch of its own.
- `Result<T>` for expected failures; exceptions for genuinely exceptional conditions.
- No empty catch blocks. No catch-log-continue that hides a failure from the caller.

Frontend mirror: a global `ErrorHandler` and a typed HTTP error interceptor, both documented in
`frontend/CLAUDE.md` and `angular-conventions`. Swallowing an error in `catchError` is **caught by
review, not by lint** — it is a rule in `angular-reviewer`. No ESLint rule can see it: `no-empty`
fires on an empty block, and `catchError(() => of(null))` has no empty block. Do not describe it as
lint-enforced.

### 6.5 Frontend standards

Compile-time and lint-time:

- `tsconfig`: `strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`, `noImplicitOverride`,
  `noFallthroughCasesInSwitch`, and `angularCompilerOptions.strictTemplates`.
  **Documented, not pre-written** — `ng new` generates this file and would overwrite anything placed first.
- `frontend/eslint.config.js`, flat config: `angular-eslint` recommended plus template a11y rules,
  `typescript-eslint` strict-type-checked, no `any`, component selector prefix, and the Angular
  lifecycle rules (`use-lifecycle-interface`, `no-empty-lifecycle-method`).
- `ng lint --max-warnings 0`, so one lint warning is a non-zero exit.

**RxJS lifecycle rules are review-enforced, not lint-enforced.** There is no RxJS ESLint plugin in
`frontend/eslint.config.js`, so `takeUntilDestroyed` usage and subscription leaks are checked by
`angular-reviewer` and by the conventions below. Adding `eslint-plugin-rxjs-x` would make them
mechanical; that is a deliberate deferral, not an oversight.

Conventions, documented in `angular-conventions`:

- Standalone components only; `inject()` over constructor injection.
- Signals for local state; `ChangeDetectionStrategy.OnPush` everywhere.
- New control flow (`@if` / `@for`) over legacy structural directives.
- `takeUntilDestroyed` for subscription lifecycle; no manual `Subscription` bookkeeping.
- Typed reactive forms; no `FormGroup<any>`.

### 6.6 Secrets

`no-secrets.ps1` blocks writes to `appsettings*.json` containing a populated `Password=`, `Pwd=`,
`AccountKey=`, `SharedAccessSignature`, or a JSON key with a non-placeholder value matching
`"[A-Za-z]*(Secret|ApiKey|SigningKey|PrivateKey|Token|Password|Pwd|AccountKey|AccessKey)[A-Za-z]*"`,
plus a literal `"Key"`. The JSON-key list includes `Password`, `Pwd` and `AccountKey` because the
`Key=value;` connection-string detector only matches that syntax and never fires on a bare JSON
key/value pair such as `"Password": "hunter2"`.

Two details are deliberate:

- **Affixes are tolerated** (`[A-Za-z]*` either side). An exact-word alternation missed the key
  names real config actually uses — `AccessKeyId`, `SecretAccessKey`, `StorageAccountKey` — which
  is a complete AWS credential pair walking straight through the guard. The literal `"Key"` is
  listed because the ASP.NET convention for a JWT signing secret is `"Jwt": { "Key": "..." }`.
  The cost is some over-blocking (`"TokenEndpoint"` now trips the guard); over-blocking a URL is
  cheaper than committing a signing key, and the placeholder allowlist absorbs the common cases.
- **`ConnectionString` keys are judged on their value, not their name.** Flagging every
  `ConnectionString` was a false positive:
  `"Server=.;Database=App;Trusted_Connection=True;"` contains no secret at all. A connection-string
  key is a finding only when its **value** carries a populated `Password=`, `Pwd=` or `AccountKey=`.

Allowed as placeholders: empty string, `${...}`, `#{...}`, `<...>`, `REPLACE_ME`, `CHANGEME`, `TODO`.
Real values belong in user-secrets or environment variables.

### 6.7 Migrations

`protect-migrations.ps1` blocks `Edit` / `MultiEdit` / **`Write`** on `**/Migrations/*.cs`, including
`*.Designer.cs` and `*ModelSnapshot.cs`, **when the target file already exists**. Creating new
migration files stays permitted. The block message directs the user to generate a new migration
rather than mutate an applied one.

`Write` is in the matcher deliberately. It overwrites an existing file just as readily as it creates
a new one, so restricting the guard to `Edit`/`MultiEdit` left a one-word bypass: an agent blocked on
`Edit` could reissue the same change as `Write`. What separates a legitimate `dotnet ef migrations
add` from a mutation is not the tool but whether the target exists, so existence — `Test-Path
-LiteralPath` — is what the hook tests.

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
| `protect-migrations` | PreToolUse | `Edit`, `MultiEdit`, `Write` | changes to **existing** migrations per §6.7 |
| `format-and-lint` | PostToolUse | `Edit`, `MultiEdit`, `Write` | nothing on `.cs`; lint errors on `.ts`/`.html` |
| `verify-build` | Stop | — | turn-end while the build is not warning-clean |

### 7.1 The feedback asymmetry

The two stacks cannot get the same loop, and the design does not pretend otherwise.

- **Frontend:** `eslint --fix` then `prettier --write` per file, roughly a second. Surviving lint
  errors `exit 2`, so they return to Claude and get fixed in the same turn. Both tools run with the
  working directory pushed to `frontend/`, because ESLint 9 resolves its flat config from the cwd
  and `eslint.config.js` lives there, not at the repo root the hook inherits. Only **exit code 1**
  is treated as lint errors; ESLint's `>= 2` means a fatal or config error, which is a toolchain
  problem and therefore exits 0 silently per the contract above — reporting it as lint errors told
  Claude about problems the file did not have.
- **Backend:** there is no fast per-file C# analyzer check — analyzers require a build. `.cs` files
  get `dotnet format whitespace` on write; warnings surface at build time through warnings-as-errors,
  the `verify-build` Stop hook, and `/verify`.

Net effect: TypeScript self-corrects immediately, C# at the next build.

### 7.2 Stop-hook loop safety

`verify-build.ps1` must read `stop_hook_active` from its payload and `exit 0` immediately when it is
true. Without that check, a build that cannot be made to pass would loop the session indefinitely.
That guard is first, unconditional, and silent — it fires on every legitimate second pass.

It also exits 0 when there is nothing to build, which is the state today. Finding what to build is
deliberately broader than a single root-level `*.sln`: it accepts `.sln` **and `.slnx`** (the .NET 9+
format), searches subdirectories (skipping `node_modules`, `bin`, `obj`, `.git`), and falls back to
building the repo root when any `.csproj` exists without a solution. Each of those layouts previously
turned the Stop gate into a permanent no-op.

Every skip **except the loop guard and an unparseable payload** writes one line to stderr saying why.
A gate that skips silently is indistinguishable from a gate that passed, which is exactly how the
root-only `*.sln` search went unnoticed.

---

## 8. `settings.json`

**Permissions.** Allow the loop actually used, so the same approvals stop recurring:
`dotnet build|test|restore|format|ef|sln|user-secrets`, `dotnet --list-sdks`, `npm ci`, `npm test *`,
`npm run *`, `npx ng *`, `node --version`, the hook suite
(`powershell.exe ... -File .claude/hooks/tests/run-hook-tests.ps1`), and read-only git (`status`,
`diff`, `log`, `branch`, `show`).

`dotnet new` is **not** allowed: scaffolding creates project structure, which is a decision to make
deliberately rather than one to pre-approve. `dotnet user-secrets` **is** allowed — it is the
sanctioned alternative to putting a secret in `appsettings.json` (§6.6), so making it prompt would
push work toward the thing the framework is trying to prevent. `npm install` is likewise absent in
favour of `npm ci`, which respects the lockfile. Together these cover every step of `/verify`, which
otherwise stopped for approval five times.

Deny outright: reads of `**/.env`, `**/secrets.json`, `**/*.pfx`; and `dotnet ef database drop`.
Ask: `git push`.

**Hooks.** The five above, wired by event. Project hooks merge with the user-level Orca hooks rather
than replacing them.

`settings.local.json` is gitignored for personal overrides; a committed
`settings.local.json.example` documents the shape. List-typed keys such as `hooks.Stop` **merge**
across `settings.json` and `settings.local.json` rather than one overriding the other, so an empty
`Stop` array locally does not disable `verify-build` (D9). Turning the Stop gate off means deleting
the `Stop` block from the shared, committed `settings.json` — a team decision — or setting
`disableAllHooks: true` locally, which disables all five hooks, not just the Stop gate.

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
| `Domain` file under a prefixed folder `src/AiFramework.Domain/` with a banned `using` | exit 2 |
| `src/DomainServices/` file with the same content (not a layer) | exit 0 |
| A Windows **backslash** `file_path` in `Domain` with a banned `using` | exit 2 |
| `appsettings.json` with `Password=hunter2` | exit 2 |
| `appsettings.json` with `Password=${DB_PASSWORD}` | exit 0 |
| `appsettings.json` with `"SecretAccessKey": "wJalrX..."` | exit 2 |
| `appsettings.json` with `"Jwt": { "Key": "..." }` | exit 2 |
| `appsettings.json` with a `Trusted_Connection=True;` connection string | exit 0 |
| `Edit` **or `Write`** over an existing `Migrations/*.cs` | exit 2 |
| `Write` to a `Migrations/*.cs` path that does not exist | exit 0 |
| `format-and-lint` with `dotnet`/`npx` absent | exit 0, silent |
| `verify-build` with `stop_hook_active: true` | exit 0, silent |
| `verify-build` with nothing to build | exit 0, one stderr line saying why |
| Malformed payload | exit 0, silent |

Everything else in the framework is text and will be **unexecuted until the toolchain is installed
and the solution scaffolded.** The plan must not claim otherwise.

---

## 14. Risks and open items

| Risk | Mitigation |
|---|---|
| Hooks are unrunnable against real builds today | Fixture tests cover hook logic; toolchain-absent paths explicitly tested |
| `verify-build` friction once the SDK lands | Disable by deleting the `Stop` block from shared `settings.json`, or all-or-nothing via local `disableAllHooks`; documented in `settings.local.json.example` (D9) |
| Per-layer `CLAUDE.md` files are speculative until code sits beside them | Kept to 10–25 lines; revisit after the first real feature |
| Version placeholders could go stale or be forgotten | Marked explicitly in root `CLAUDE.md` as fill-at-scaffold |
| `ng new` overwriting pre-written frontend config | Only `eslint.config.js` and `.prettierrc` are pre-written; `tsconfig` changes are documented as a post-`ng new` step |
| Root namespace assumed `AiFramework` | Read from `hooks.config.json`; single-line change |

### Deferred to a later pass

1. Install .NET SDK and Node; pin real versions into root `CLAUDE.md`.
2. Scaffold the solution and Angular workspace.
3. Apply the documented `tsconfig` delta.
4. Run `run-hook-tests.ps1` against real files, and `/verify` end to end.
