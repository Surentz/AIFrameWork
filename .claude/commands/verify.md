---
description: Build, test, and lint both stacks and report what actually ran
---

Verify the repo. **Check each toolchain before using it**, and report honestly on anything
that could not run — a skipped step is never reported as a pass.

## 1. Backend

```
dotnet --list-sdks
```

Prints nothing? There is no .NET SDK. Say so, skip to step 2, do not report a failure.

Otherwise:

```
dotnet format whitespace AiFramework.slnx --verify-no-changes --verbosity quiet
dotnet build --nologo --verbosity quiet
dotnet test --nologo --verbosity quiet
```

The format check is CI's `Check formatting` step in `backend (Debug)`. It fails on a diff, not a
build error, so report each `file:line` it prints. Do not fix them silently: say they need
`dotnet format whitespace AiFramework.slnx`, and run it only if asked.

Warnings are errors here, so a warning is a build failure. Report every diagnostic with
`file:line`.

The three Testcontainers projects (`Infrastructure.Tests`, `Api.IntegrationTests`,
`Worker.IntegrationTests`) need a running Docker daemon — for Postgres, for RabbitMQ in the two
integration projects, and for Keycloak in `Infrastructure.Tests`' external-system tests. If
`docker info` fails, say so: their failures are environmental, not assertion failures.

A green certificate/TLS test on Windows is not evidence about Linux: Windows completes
certificate chains from its own store. Say so when reporting `Infrastructure.Tests` from a
Windows machine; CI's Linux `backend` jobs are the authority for those.

## 2. Frontend

```
node --version
```

Fails? Node is not installed. Say so and skip to step 3.

`frontend/node_modules` missing? Say it needs `npm ci --prefix frontend` and skip.

Otherwise:

```
npm run lint --prefix frontend
npm run build --prefix frontend
npm test --prefix frontend -- --run
```

`npm run lint` runs `eslint . --max-warnings 0`: one warning is a failure.

## 3. Generated code is current

**This is the step that most often separates a green `/verify` from a red CI.** Both checks
below are CI jobs of their own (`codegen`, `contract`), and both fail on a *diff* rather than on
a build error — so the working tree can build, test, and lint clean while CI rejects it. Skip
this step only if step 1 found no SDK.

### 3a. Wolverine adapters — TWO trees, not one

```
ConnectionStrings__Default='Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder' \
  Wolverine__Durable=false \
  dotnet run --project src/Api -- codegen write
ConnectionStrings__Default='Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder' \
  Wolverine__Durable=false \
  dotnet run --project src/Worker -- codegen write
git diff --exit-code -- src/Api/Internal src/Worker/Internal
```

Both variables are required: `codegen write` boots the host, the startup guard in `Program.cs`
rejects an empty connection string, and a durable Wolverine would dial Postgres. The connection
string is never opened.

A non-empty diff means the committed adapters are stale. Debug compiles adapters at startup and
stays green either way; only **Release** breaks, at startup. Report the diff and say the fix is
to commit the regenerated tree.

**If the only difference is statement ordering inside a handler you did not change, keep the
committed version.** Windows and Linux each produce a stable but different order, and CI's Linux
output is the authority — see the `regenerate` skill.

### 3b. The API contract

Skip this half if step 2 found no Node or no `frontend/node_modules` — it needs both.

```
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false Admin__ReconcileOnStart=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
git diff --exit-code -- openapi/ frontend/src/api/schema.d.ts
```

All three variables are required, exactly as in CI's `contract` job: without
`Admin__ReconcileOnStart=false` the startup administrator reconciler dials Postgres and the
generator fails with an `ObjectDisposedException` that names nothing (ADR 0020).
`dotnet restore` is a separate first step on purpose: `dotnet msbuild` does not restore
implicitly, and folding it in as `-t:"Restore;Build;..."` fails with CS9137 instead. A non-empty
diff means a controller, DTO, or `[ProducesResponseType]` changed without the contract being
regenerated.

## 4. End-to-end

```
docker info
```

Fails? Docker is not running. Say so and skip to step 5 — a skipped e2e run is never
reported as a pass.

`frontend/node_modules` missing? Same skip as step 2.

First time on this machine? `prepare-database.ts` does not install Playwright's browser binary
itself. `scripts/install-prereqs.ps1` handles this (its "Playwright browser" check runs
`npx playwright install chromium`, a no-op if it is already present) — run it, or run
`npx --prefix frontend playwright install chromium` directly (documented in `frontend/CLAUDE.md`,
"Before the first `npm run e2e`"), or this step fails with a missing-browser error rather than an
assertion failure.

Otherwise:

```
npm run e2e --prefix frontend
```

This starts Postgres and RabbitMQ containers, applies migrations, and runs the API, the job
worker and a preview build under Playwright. It binds 5234 and 5235, so a running dev loop
(`scripts/dev.ps1`) has to be stopped first or this fails to start rather than failing a test. It is the slowest step and the one most likely to fail environmentally —
report the distinction between an environmental failure and an assertion failure.

## 5. Hooks

The hook suite is PowerShell, and so are the hooks it tests. Check for a shell before using one:

```
pwsh -Version
```

Fails? Try `powershell.exe -Version` (Windows PowerShell). **Neither available — on Linux or
macOS without PowerShell installed — is a skip, not a pass.** Say so explicitly, and say what it
means: the five hooks in `.claude/settings.json` are invoked as `powershell.exe`, so on this
machine none of them are running either. `dependency-rule.ps1`, `no-secrets.ps1`, and
`protect-migrations.ps1` are not enforcing anything here, and the layering rules are carried by
the architecture tests in step 1 and by review alone.

With a shell available:

```
pwsh -NoProfile -File .claude/hooks/tests/run-hook-tests.ps1
```

(or `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/hooks/tests/run-hook-tests.ps1`)

## Report

A table: step, ran or skipped, result. Then the failures with `file:line`. State plainly which
steps were skipped and why. Never summarise a skipped step as passing.

Steps 1, 2, 3 and 4 correspond to CI's `backend`, `frontend`, `codegen`+`contract`, and `e2e`
jobs. CI does two things this command does not: it **builds and tests Release as well as
Debug** — so a clean `/verify` is not by itself evidence that Release starts — and it checks the
PR title is a Conventional Commit (`pr title and labels`).
