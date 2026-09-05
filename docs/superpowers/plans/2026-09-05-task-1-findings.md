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
