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
dotnet build --nologo --verbosity quiet
dotnet test --nologo --verbosity quiet
```

Warnings are errors here, so a warning is a build failure. Report every diagnostic with
`file:line`.

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

`ng lint` runs with `--max-warnings 0`: one warning is a failure.

## 3. Hooks

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/hooks/tests/run-hook-tests.ps1
```

This one always runs — it has no toolchain dependency.

## Report

A table: step, ran or skipped, result. Then the failures with `file:line`. State plainly which
steps were skipped and why. Never summarise a skipped step as passing.
