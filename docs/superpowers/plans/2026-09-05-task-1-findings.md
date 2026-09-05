# Task 1 findings — build-time OpenAPI generation without a database

**Date:** 2026-09-05
**Outcome:** **Option A works**, with a correction — it needs **two** environment variables, not one.
**Decision:** Task 3 proceeds as written, with the amendments in "Corrections to the plan" below.

## The question

`Microsoft.Extensions.ApiDescription.Server` generates the document by running the application.
Durable Wolverine connects to PostgreSQL during host startup (ADR 0005), so a build would need
a database — fatal in CI. The mitigation was assumed to be `Wolverine__Durable=false`, but
whether that value reaches an MSBuild-spawned process was unverified.

## Method

The dev and e2e containers were stopped first and port 55433 confirmed refusing, so the probe
could not pass by reaching a database that happened to be up. `openapi/` was confirmed absent,
so a stale document could not be mistaken for a fresh one.

## What happened

### Run 1 — `Wolverine__Durable=false` only

**Failed, but not for the predicted reason.**

```
error : System.InvalidOperationException: ConnectionStrings:Default is not configured.
   at Program.<Main>$(String[] args) in src/Api/Program.cs:line 15
   at Microsoft.Extensions.Hosting.HostFactoryResolver.HostingListener.CreateHost()
   at Microsoft.Extensions.ApiDescription.Tool.Commands.GetDocumentCommandWorker.Process()
```

The generator runs the app with no `ASPNETCORE_ENVIRONMENT`, so `appsettings.Development.json`
never loads, and `appsettings.json` ships `"Default": ""`. The startup guard in `Program.cs`
fires before Wolverine is reached at all.

**This blocker was not in the spec.** The spec anticipated the Wolverine connection and missed
the connection-string guard standing in front of it.

### Run 2 — `ConnectionStrings__Default` only, Wolverine left durable

**Failed with the predicted error:**

```
Npgsql.NpgsqlException: Failed to connect to 127.0.0.1:55433
```

Two things established here, both load-bearing:

1. **Environment variables do propagate** to the spawned generator process. The error changed
   from "not configured" to "failed to connect", which only happens if the value arrived.
2. **The generator starts the host, it does not merely build it.** `HostFactoryResolver`
   appears in the stack, and Wolverine's startup migration ran and tried to connect. So
   `Wolverine__Durable=false` is genuinely required — it is not redundant.

### Run 3 — both variables, database still down

**Succeeded.** `Build succeeded. 0 Warning(s) 0 Error(s)`, and the document was written.

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet build src/Api -c Debug
```

The connection string is never opened — it only has to be non-empty to satisfy the guard, and
`Wolverine__Durable=false` stops anything from dialling it.

## Corrections to the plan

**1. Two environment variables, not one.** Every place the plan writes
`Wolverine__Durable=false dotnet build src/Api` must also set `ConnectionStrings__Default` to a
placeholder. This affects Task 3 Step 3, Task 3 Step 6 (the `CLAUDE.md` snippet), Task 5 Step 8,
and Task 6's CI job.

**2. The output file is `openapi/AiFramework.Api.json`, not `openapi/v1.json`.** The generator
names the file after the assembly. Either accept that name or set `OpenApiDocumentsFileName`.
Affects Task 3 Step 4, Task 5 Step 2's `generate:api` script, and Task 6's drift paths.

**3. `/health` is in the document.** The minimal-API health endpoint is described alongside the
controller routes. Harmless, but Task 3 Step 4's sanity check should not treat its presence as
a surprise.

## Confirmed, unchanged

**Schema names are exactly as Task 5 assumes:** `OrderResponse`, `OrderListItemResponse`,
`OrderPageResponse`, `PlaceOrderRequest`. Task 5 Step 4's aliases need no adjustment.

**`nextCursor` will generate correctly.** The document has
`{"type":["null","string"]}` with `nextCursor` in `required`, so `openapi-typescript` emits a
required nullable — matching the hand-written `readonly nextCursor: string | null` exactly. The
spec's `exactOptionalPropertyTypes` risk does not materialise for this property.

**Spec section 4 is confirmed.** The generated `ProblemDetails` schema carries only
`type, title, status, detail, instance`. `errors` and `traceId` are absent, as predicted, so the
hand-written interface in `client.ts` and the guarding test in Task 4 are both necessary.

**The document is OpenAPI 3.1.1**, titled `AiFramework.Api | v1`.

## Housekeeping

All probe changes were reverted (`git checkout` on the csproj and `Program.cs`, `openapi/`
deleted); the working tree was confirmed clean. Nothing from this task is kept.

The dev database is left **stopped**. Start it with `docker compose up -d --wait` before running
the test suite.

---

## Correction, found during Task 3 — this document was wrong

**The "Corrections to the plan" section above under-generalised, and it mattered.**

It listed the *commands* that needed both environment variables. The real consequence is broader:
with `OpenApiGenerateDocumentsOnBuild=true`, **every `dotnet build` of `src/Api` needs them** —
including a plain `dotnet build` with the database running, and `dotnet publish`. Verified:

```
$ dotnet build -c Debug          # dev database UP, no env vars
error : System.InvalidOperationException: ConnectionStrings:Default is not configured.
Build FAILED.  10 Error(s)
```

That is not a documentation gap. It would have broken every developer's build, and the existing
CI `backend` (both configurations) and `e2e` jobs, neither of which Task 6 touches. The Task 3
subagent caught it, correctly declined to edit CI because it was outside its task scope, and
escalated instead.

### The fix: Option B after all

`OpenApiGenerateDocumentsOnBuild` is now **false**, and generation runs as an explicit target:

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
```

Verified end to end: a plain `dotnet build` succeeds again with no environment variables,
`dotnet publish` succeeds, and the explicit target still produces a document **byte-identical**
to the committed one with the database stopped.

Option A was reported as "works" in this document because the only question asked was whether the
document could be generated without a database. It could. The question not asked was what that
setting costs every *other* build — and that is where the real problem was.

### Also corrected

**Publish size is ~19MB, not ~17MB.** The 2MB increase is `Scalar.AspNetCore.dll` (1.4MB),
`Microsoft.OpenApi.dll` (475KB) and `Microsoft.AspNetCore.OpenApi.dll` (189KB) — real libraries
from Task 2, not a Roslyn-style leak. `Microsoft.Extensions.ApiDescription.Server` is correctly
absent from the output, so its `PrivateAssets` works. Task 3 Step 5 and Task 7 Step 2 now expect
19MB.

Note for later: Scalar ships 1.4MB into production despite being mapped only in Development,
because the code references its types and so must compile in Release. Making it Debug-only would
need `#if` around the mapping. Not worth it for 1.4MB, but worth knowing it is there.

---

## Second correction, found during Task 5 — the target alone generates from a STALE assembly

`dotnet msbuild src/Api -t:GenerateOpenApiDocuments` does **not** rebuild first. It reads the
already-compiled DLL, so after a source change it silently regenerates the *old* contract.

Caught by Task 5's Step 8 drift proof, which is the only step that tests the feature rather than
the plumbing: a backend property was renamed, the document was regenerated, and it came back
unchanged. Without that step this would have shipped, and the committed document would have
drifted from the code the first time anyone edited a DTO — with CI's drift job reporting
"no change" and confirming the wrong answer.

**The command is therefore `-t:"Build;GenerateOpenApiDocuments"`, not `-t:GenerateOpenApiDocuments`.**
Verified: touch a source file, run the combined form, and the document reflects the change.
Corrected in `CLAUDE.md`, the csproj comment, this plan, and the CI job.

## Third correction — Task 5's Step 8 as written cannot work

The plan's drift demonstration renames `Sku` in `OrderDtos.cs`. That **breaks the C# build**:
`OrdersController` maps `.Sku` in three places, so nothing regenerates and the frontend is never
reached. The step was written assuming the backend would still compile.

The demonstration that does work changes the *wire* contract while leaving C# untouched:

```csharp
[JsonPropertyName("productCode")]
public required string Sku { get; init; }
```

on `OrderResponse` only. C# still compiles, the document changes, the generated types change, and
the frontend build fails with exactly:

```
src/features/orders/OrderDetail.tsx(18,17): error TS2339: Property 'sku' does not exist on type
  '{ readonly id: string; readonly productCode: string; ... }'.
src/test/handlers.ts(10,3): error TS2353: Object literal may only specify known properties, and
  'sku' does not exist in type '{ readonly id: string; readonly productCode: string; ... }'.
```

Both failures matter. `OrderDetail.tsx` is the app; `handlers.ts` is the MSW fixture — the exact
place drift used to hide, since a stale fixture kept the tests green while the app broke. Typing
that fixture is what converts a silent failure into a build error.

Reverting produced a byte-identical document and schema, so the round-trip is clean.

## Fourth correction — openapi-typescript cannot be a devDependency here

Every 7.x version, including 7.13.0, declares `peer typescript@"^5.x"`. This repo pins
TypeScript 6.0.3, so `npm install` refuses.

`--legacy-peer-deps` is **not** an acceptable answer: it installs, but re-resolves the tree and
breaks `@testing-library/react`, failing six test files with
`Module '"@testing-library/react"' has no exported member 'screen'`. That is npm's warned-about
"potentially broken dependency resolution", and it has nothing to do with this feature.

The generator is a build-time CLI, not a library the app imports, so it does not need to be in
the dependency tree at all. The `generate:api` script runs it through `npx --yes
openapi-typescript@7.13.0`, which pins the version, leaves `package.json` and
`package-lock.json` untouched, and produces byte-identical output.

## Fifth item — two accepted deviations, approved before proceeding

**`quantity` generates as `number | string`.** Not a generator fault: ASP.NET Core's
`JsonSerializerDefaults.Web` sets `NumberHandling = AllowReadingFromString`, so the document
honestly declares `type: ["integer","string"]` and the API really does accept `"5"`. Accepted as
the truthful contract rather than narrowed. Nothing in the frontend does arithmetic on it, so
nothing breaks.

**ESLint ignores the generated file.** `src/api/schema.d.ts` trips
`consistent-indexed-object-style` under `--max-warnings 0`. It is added to the existing
`ignores` list beside `dist/`, `coverage/` and `playwright-report/` — declaring it as
non-authored output, not relaxing the rule for hand-written code.

---

## Sixth correction, found by CI — `dotnet msbuild` does not restore

The `contract` job failed on its first real run:

```
error NETSDK1004: Assets file '/home/runner/.../src/Api/obj/project.assets.json' not found.
Run a NuGet package restore to generate this file.
```

It passed locally only because `obj/` was already warm. `dotnet msbuild` does not restore
implicitly the way `dotnet build` does, and the documented regenerate command had the same bug —
it would have failed on any fresh clone.

`-t:"Restore;Build;GenerateOpenApiDocuments"` is **not** the fix. Verified from a cold `obj/`, it
fails differently:

```
error CS9137: The 'interceptors' feature is not enabled in this namespace. Add
'<InterceptorsNamespaces>$(InterceptorsNamespaces);Microsoft.AspNetCore.OpenApi.Generated</InterceptorsNamespaces>'
```

MSBuild evaluates the project once, before Restore writes NuGet's props, so the OpenAPI
XML-comment source generator never sees the properties it needs. Restore has to be its own
invocation — which is exactly why `dotnet build` runs it as a separate phase internally.

The fix is a separate `dotnet restore src/Api` step, in CI and in the documented command.
Verified from a fully cold `obj/` and `bin/`: restore, then `-t:"Build;GenerateOpenApiDocuments"`,
produces a byte-identical document.
