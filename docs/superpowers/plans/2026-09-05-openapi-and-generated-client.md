# OpenAPI and Generated Frontend Client — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Serve a browsable OpenAPI document in Development, and generate the React app's TypeScript payload types from it so the frontend cannot silently disagree with the backend contract.

**Architecture:** The built-in .NET 10 OpenAPI generator produces the document; Scalar renders it, both behind an `IsDevelopment()` check. `Microsoft.Extensions.ApiDescription.Server` writes the same document to a committed `openapi/AiFramework.Api.json` at build time, and `openapi-typescript` turns that file into `frontend/src/api/schema.d.ts` (types only, no runtime code). Both generated artifacts are committed and guarded by a CI drift job, matching the existing Wolverine codegen pattern.

**Tech Stack:** .NET 10 / ASP.NET Core, `Microsoft.AspNetCore.OpenApi`, `Scalar.AspNetCore`, `Microsoft.Extensions.ApiDescription.Server`, `openapi-typescript`, React 19 + TypeScript, xUnit + FluentAssertions, Vitest + MSW.

**Spec:** `docs/superpowers/specs/2026-09-05-openapi-and-generated-client-design.md`

## Global Constraints

- **Versions are pinned and were verified 2026-09-05.** `Microsoft.AspNetCore.OpenApi` **10.0.11**, `Microsoft.Extensions.ApiDescription.Server` **10.0.11**, `Scalar.AspNetCore` **2.17.2**, `openapi-typescript` **7.13.0**. Do not use 11.0.0 — it exists only as previews.
- **Warnings are errors** from compiler, analyzers and build (`Directory.Build.props`). A warning fails the build. Do not suppress without a justification comment; repo-wide exemptions live in `.editorconfig`.
- **The dependency rule** is hook-enforced (`.claude/hooks/dependency-rule.ps1`). All backend changes here are in `src/Api`, which may reference Infrastructure for DI only.
- **Nullable is enabled.** `required` in Domain, never `[Required]`.
- **No secrets in `appsettings*.json`.**
- **Exposure is Development-only.** Neither the document nor the UI may be reachable when the environment is not Development.
- **Generated TypeScript must be `readonly`** — `tsconfig` sets `strict`, `noUncheckedIndexedAccess` and `exactOptionalPropertyTypes`, and the hand-written types it replaces are `readonly` throughout.
- **No runtime dependency may be added to the frontend.** `openapi-typescript` is a devDependency and emits types only.
- **Commands:** backend `dotnet build`, `dotnet test`; frontend `npm run <script> --prefix frontend`.
- **Never commit without the build and the full test suite green.**

## Guardrails for executors

**Every one of these means: stop and report. Do not improvise past a blocked step.**
A task reported as blocked is a good outcome. A task reported as green that quietly defeated
its own purpose is the failure this section exists to prevent.

**Never suppress a diagnostic to reach green.** No new `#pragma warning disable`, no
`severity = none` in `.editorconfig`, no `<NoWarn>`. This repo runs SonarAnalyzer, Meziantou
and AsyncFixer alongside the .NET analyzers under warnings-as-errors, and genuine catch-22s
happen — one analyzer demanding `static` while another forbids it is a real case that has
already occurred here. When you hit one, report the exact diagnostic IDs and what each demands.
Do not pick a suppression yourself.

**Never weaken TypeScript to make it compile.** No `as any`, no `@ts-expect-error`, no `!`
non-null assertion, no edits to `tsconfig.*.json` or the eslint config. The strictness
(`strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`) is what makes the
generated types load-bearing; loosening it to accommodate them destroys the feature while
leaving the build green.

**Never hand-edit a generated file.** `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts` and
`src/Api/Internal/Generated/**` are outputs. If one is wrong, the generator or its input is
wrong. Editing the output makes CI's drift job fail and hides the real cause.

**Never delete, skip or weaken an existing test** to make a change pass. If an existing test
now fails, that is a finding — report it with the failure output. The 116 tests currently
passing are the baseline.

**Never add a package that is not named in Global Constraints.** If a step seems to need one,
report that instead.

**No sleeps in tests.** No `Task.Delay`, no `Thread.Sleep`, no retry-until-timeout —
`tests/CLAUDE.md` forbids them. Deterministic waits only.

**When a step's expected output does not match, stop.** Every step states what to expect
precisely so that a mismatch is informative. A mismatch is a signal, not an obstacle to route
around.

**Report what actually happened.** If a step was skipped, say so. If tests fail, quote the
output. Never describe work as complete that was not run.

---

### Task 1: Prove build-time document generation can run without a database

> **DONE — 2026-09-05.** Option A works, but needs **two** environment variables, not one.
> Findings, including two corrections already applied to the tasks below:
> `docs/superpowers/plans/2026-09-05-task-1-findings.md`. Kept here as the record of why
> Task 3 looks the way it does.

This is a **spike**, not a feature. Its output is an answer that decides Task 3's shape. Nothing built here is kept unless it happens to be the answer.

The spec's highest risk: `Microsoft.Extensions.ApiDescription.Server` generates the document by *running the application*, and durable Wolverine opens a PostgreSQL connection during host startup (ADR 0005). A build would therefore need a database — fatal in CI. The known mitigation is `Wolverine__Durable=false`, but how that value reaches an MSBuild-spawned process is unverified.

**Files:**
- Modify (temporarily): `src/Api/AiFramework.Api.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: a decision recorded in the task's commit message — which of options A/B/C below Task 3 uses. No code.

- [x] **Step 1: Stop the dev database so the probe cannot pass by accident**

```bash
docker compose down
docker ps --format "{{.Names}}"
```

Expected: no `aiframework-dev-postgres-1`. If it is still running, the probe proves nothing — a build could reach the database and you would not learn whether the mitigation works.

- [x] **Step 2: Add the package and enable document generation (Option A — plain build)**

In `src/Api/AiFramework.Api.csproj`, inside the existing `<ItemGroup>` with the `ProjectReference` entries, add a new `<ItemGroup>`:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.11" />
    <PackageReference Include="Microsoft.Extensions.ApiDescription.Server" Version="10.0.11">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <PropertyGroup>
    <OpenApiDocumentsDirectory>$(MSBuildThisFileDirectory)../../openapi</OpenApiDocumentsDirectory>
    <OpenApiGenerateDocumentsOnBuild>false</OpenApiGenerateDocumentsOnBuild>
  </PropertyGroup>
```

Also add the minimum registration needed for a document to exist — in `src/Api/Program.cs`, after `builder.Services.AddExceptionHandler<GlobalExceptionHandler>();`:

```csharp
builder.Services.AddOpenApi();
```

- [x] **Step 3: Build with the environment variable set, and see what happens**

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
```

Expected outcome is unknown — that is the point. Record which happens:

- **Document written, build succeeds** → Option A works. The environment propagates.
- **Build fails with `Failed to connect to 127.0.0.1:55433`** → the environment did not reach the spawned process. Go to Step 4.
- **Build fails some other way** → read the error; it may be an unrelated wiring problem in Step 2.

- [x] **Step 4: If Option A failed, try Option B — generation off the build, on an explicit target**

Change `OpenApiGenerateDocumentsOnBuild` to `false` in the csproj, then invoke the target directly with the environment set:

```bash
Wolverine__Durable=false dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
```

Expected: the document appears at `openapi/AiFramework.Api.json` without a database.

- [x] **Step 5: If Option B failed, try Option C — set the value where the app reads it, not where MSBuild runs**

Revert the environment approach and instead make the *application* default to non-durable when it is being run for document generation. `ApiDescription.Server` invokes the app through `GetDocument.Insider`; check for its marker rather than guessing. Inspect what the app receives:

```bash
Wolverine__Durable=false dotnet build src/Api -c Debug 2>&1 | tee /tmp/probe.log
grep -iE "getdocument|insider|--document" /tmp/probe.log | head
```

If a distinguishable signal exists (an argument, an assembly, an environment variable), Option C is: read it in `Program.cs` and pass `durable: false` to `AddWolverineEventPath`. Record the exact signal.

- [x] **Step 6: Revert everything from this task**

```bash
git checkout -- src/Api/AiFramework.Api.csproj src/Api/Program.cs
git status --short
```

Expected: clean. This was a probe; Task 2 and Task 3 build the real thing.

- [x] **Step 7: Record the decision**

Create `docs/superpowers/plans/2026-09-05-task-1-findings.md` with: which option worked, the exact command or code that made it work, and the verbatim error from any that did not.

**If none of A, B or C works:** stop and report. The spec says this argues against committing the document at all, and the design should be revisited rather than forced. Do not proceed to Task 3; Tasks 2, 4, 5 and 6 are still valid without a committed document, and Task 5 can generate from a running API instead.

```bash
git add docs/superpowers/plans/2026-09-05-task-1-findings.md
git commit -m "docs: record how build-time OpenAPI generation avoids the database"
```

---

### Task 2: Serve the OpenAPI document and Scalar UI, Development only

**Files:**
- Modify: `src/Api/AiFramework.Api.csproj`
- Modify: `src/Api/Program.cs`
- Modify: `src/Api/Orders/OrdersController.cs`
- Create: `tests/Api.IntegrationTests/OpenApiDocumentTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 (that was a probe).
- Produces: `/openapi/v1.json` served in Development; `/scalar/v1` UI in Development. Task 3 relies on `AddOpenApi()` being registered. Task 5 relies on the document declaring schemas named `OrderResponse`, `OrderListItemResponse`, `OrderPageResponse` and `PlaceOrderRequest`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Api.IntegrationTests/OpenApiDocumentTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests;

/// <summary>
/// The document is a Development-only convenience, and that is a security property rather than
/// a preference: a deployed instance must not publish its endpoint surface. Both halves are
/// asserted, because only asserting the happy path would let a missing environment check ship.
///
/// Container-free on purpose, like HealthTests: neither endpoint touches the database, so the
/// placeholder connection string plus Wolverine:Durable=false is enough to boot the host.
/// </summary>
public sealed class OpenApiDocumentTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string PlaceholderConnectionString =
        "Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder";

    private WebApplicationFactory<Program> ForEnvironment(string environmentName)
        => factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", PlaceholderConnectionString);
            builder.UseSetting("Wolverine:Durable", "false");
            builder.UseEnvironment(environmentName);
        });

    [Fact]
    public async Task GetOpenApiDocument_InDevelopment_DescribesTheOrdersEndpoints()
    {
        using var client = ForEnvironment("Development").CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("paths")
            .TryGetProperty("/api/orders", out _).Should().BeTrue(
                "the document is worthless if it does not describe the one controller there is");
    }

    [Fact]
    public async Task GetOpenApiDocument_OutsideDevelopment_IsNotServed()
    {
        using var client = ForEnvironment("Production").CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a deployed instance must not publish its endpoint surface");
    }

    [Fact]
    public async Task GetScalarUi_OutsideDevelopment_IsNotServed()
    {
        using var client = ForEnvironment("Production").CreateClient();

        var response = await client.GetAsync("/scalar/v1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Api.IntegrationTests -c Debug --filter "FullyQualifiedName~OpenApiDocumentTests"
```

Expected: the two Production tests PASS already (nothing is mapped, so everything 404s), and `GetOpenApiDocument_InDevelopment_DescribesTheOrdersEndpoints` FAILS with `Expected StatusCode to be OK, but found NotFound`.

This is the correct starting state: the test that must go from red to green is the one that adds behaviour, and the two guarding tests are already green and must *stay* green.

- [ ] **Step 3: Add the packages**

In `src/Api/AiFramework.Api.csproj`, add after the existing `<ItemGroup>`:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.11" />
    <PackageReference Include="Scalar.AspNetCore" Version="2.17.2" />
  </ItemGroup>
```

- [ ] **Step 4: Register and map, Development only**

In `src/Api/Program.cs`, add to the usings (keeping alphabetical order — `Scalar.AspNetCore` sorts after `AiFramework.*` and `JasperFx`):

```csharp
using Scalar.AspNetCore;
```

After `builder.Services.AddExceptionHandler<GlobalExceptionHandler>();`:

```csharp
builder.Services.AddOpenApi();
```

After `app.MapGet("/health", ...)`:

```csharp
// Development only, deliberately: a deployed instance must not publish its endpoint surface.
// Asserted in both directions by OpenApiDocumentTests, because a missing environment check
// here would otherwise ship silently.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/Api.IntegrationTests -c Debug --filter "FullyQualifiedName~OpenApiDocumentTests"
```

Expected: 3 passed.

If `MapScalarApiReference()` does not resolve, check the package's current entry point rather than guessing — introspect `~/.nuget/packages/scalar.aspnetcore/2.17.2/lib/net10.0/Scalar.AspNetCore.xml` for the extension method name, as was done for Wolverine's API.

- [ ] **Step 6: Add XML summaries so the UI is readable**

In `src/Api/Orders/OrdersController.cs`, add a `<summary>` above each action. `GenerateDocumentationFile` is already `true` and `CS1591` already suppressed, so these flow into the document with no build change.

```csharp
    /// <summary>Places an order and returns its new identifier.</summary>
```

above `Place`,

```csharp
    /// <summary>Lists orders newest first, one page at a time.</summary>
```

above `List`, and

```csharp
    /// <summary>Fetches a single order by its identifier.</summary>
```

above `Get`.

- [ ] **Step 7: Run the full backend suite**

```bash
dotnet build -c Debug && dotnet test -c Debug --no-build
```

Expected: 0 warnings, 0 errors, 119/119 (116 existing + 3 new).

- [ ] **Step 8: Look at it**

```bash
docker compose up -d --wait
dotnet run --project src/Api
```

Open `http://localhost:5234/scalar/v1`. Confirm the three operations appear with the summaries from Step 6. Stop the app with Ctrl+C.

This step has no assertion and cannot fail a build — it exists because the whole point of the feature is that a human can see the endpoints, and nothing else in this plan checks that it actually looks right.

- [ ] **Step 9: Commit**

```bash
git add src/Api/AiFramework.Api.csproj src/Api/Program.cs src/Api/Orders/OrdersController.cs tests/Api.IntegrationTests/OpenApiDocumentTests.cs
git commit -m "feat(api): serve an OpenAPI document and Scalar UI in Development"
```

---

### Task 3: Emit the document as a committed artifact

**Depends on Task 1.** Use whichever of Options A/B/C that task recorded as working. The steps below are written for **Option A**; if Task 1 found otherwise, substitute its recorded command and adjust Step 2 accordingly, keeping every other step identical.

**Files:**
- Modify: `src/Api/AiFramework.Api.csproj`
- Create: `openapi/AiFramework.Api.json` (generated, committed)
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: `AddOpenApi()` from Task 2.
- Produces: `openapi/AiFramework.Api.json` at the repository root — the input Task 5's generator reads, and the file Task 7's drift job regenerates.

- [ ] **Step 1: Add the generator package**

In `src/Api/AiFramework.Api.csproj`, add to the package `<ItemGroup>` from Task 2:

```xml
    <PackageReference Include="Microsoft.Extensions.ApiDescription.Server" Version="10.0.11">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
```

`PrivateAssets` here stops the package flowing to consumers of Api. It does **not** keep it out of Api's own publish output — see ADR 0005, where that distinction cost 33MB — so check the publish size in Step 5 rather than assuming.

- [ ] **Step 2: Point it at the repository root and enable it**

Add to `src/Api/AiFramework.Api.csproj`, inside the existing `<PropertyGroup>` that holds `UserSecretsId`:

```xml
    <!-- The document is committed at the repo root so frontend generation reads a file rather
         than a running API, and so a contract change shows up as a diff in review. -->
    <OpenApiDocumentsDirectory>$(MSBuildThisFileDirectory)../../openapi</OpenApiDocumentsDirectory>
    <OpenApiGenerateDocumentsOnBuild>false</OpenApiGenerateDocumentsOnBuild>
```

- [ ] **Step 3: Generate with the database down**

```bash
docker compose down
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
ls -la openapi/
```

Expected: `openapi/AiFramework.Api.json` exists, and the build succeeded with no database running. If it fails with `Failed to connect`, Task 1's recorded option was not applied correctly — re-read its findings file.

- [ ] **Step 4: Sanity-check the document**

```bash
node -e "const d=require('./openapi/AiFramework.Api.json');console.log('paths:',Object.keys(d.paths).join(', '));console.log('schemas:',Object.keys(d.components.schemas).join(', '))"
```

Expected paths include `/api/orders` and `/api/orders/{id}`. Expected schemas include `PlaceOrderRequest`, `OrderResponse`, `OrderListItemResponse`, `OrderPageResponse`.

If the schema names differ, **record the actual names** — Task 5 Step 4 indexes into them by name and must match.

- [ ] **Step 5: Check the publish size did not jump**

```bash
dotnet publish src/Api -c Release -o /tmp/openapi-publish
du -sm /tmp/openapi-publish
```

Expected: **~19MB**. That is 2MB above ADR 0005's 17MB figure, and the increase is accounted for: `Scalar.AspNetCore.dll` (1.4MB), `Microsoft.OpenApi.dll` (475KB) and `Microsoft.AspNetCore.OpenApi.dll` (189KB) — real libraries, not a Roslyn-style leak. `Microsoft.Extensions.ApiDescription.Server` is correctly absent, so its `PrivateAssets` is working. A figure near 50MB would mean something dragged the compiler in; investigate before committing rather than after.

- [ ] **Step 6: Document the workflow**

In `CLAUDE.md`, add a section after the existing `## Wolverine codegen` section:

```markdown
## The API contract

`openapi/AiFramework.Api.json` is generated from the running application at build time and **committed**.
`frontend/src/api/schema.d.ts` is generated from it. Both are checked by CI.

**After changing a controller, a DTO, or a `[ProducesResponseType]`, regenerate both:**

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
```

Then commit the result. **Both environment variables are required**, and neither is defensive:
document generation runs the application, so without a connection string it fails on the startup
guard in `Program.cs`, and with one but still durable, Wolverine's startup migration dials
Postgres (ADR 0005). The connection string is never actually opened — it only has to be
non-empty.
```

- [ ] **Step 7: Full verification and commit**

```bash
docker compose up -d --wait
dotnet build -c Debug && dotnet test -c Debug --no-build
git add src/Api/AiFramework.Api.csproj openapi/AiFramework.Api.json CLAUDE.md
git commit -m "feat(api): emit openapi/AiFramework.Api.json at build time and commit it"
```

Expected: 119/119.

---

### Task 4: Guard the hand-written error contract

`errors` and `traceId` are `ProblemDetails.Extensions`, so OpenAPI cannot name them and `client.ts` keeps its own `ProblemDetails` interface. That seam needs a test, because nothing currently checks the hand-written type still matches what the API returns.

**Note what already exists:** `OrdersEndpointTests.PostOrders_WithZeroQuantity_Returns400` already asserts `title` and `errors`. Do **not** duplicate that. This task covers what is genuinely unguarded: `detail` and `traceId`.

**Files:**
- Create: `tests/Api.IntegrationTests/Orders/ProblemDetailsContractTests.cs`

**Interfaces:**
- Consumes: `ApiFactory` and `ApiFactoryCollection` (existing).
- Produces: nothing other tasks use.

- [ ] **Step 1: Write the failing test**

Create `tests/Api.IntegrationTests/Orders/ProblemDetailsContractTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace AiFramework.Api.IntegrationTests.Orders;

/// <summary>
/// Pins the error body against frontend/src/api/client.ts, which declares its own
/// ProblemDetails interface and reads `detail` and `errors` off it. That interface cannot be
/// generated: ResultExtensions.Problem puts `errors` and `traceId` into
/// ProblemDetails.Extensions rather than typed properties, so OpenAPI never names them and the
/// generated schema cannot describe them.
///
/// That makes this the one part of the contract where backend and frontend can drift in
/// silence, which is exactly why it is asserted here instead of trusted.
///
/// `title` and `errors` are already covered by OrdersEndpointTests.
/// PostOrders_WithZeroQuantity_Returns400; this covers the rest rather than repeating it.
/// </summary>
[Collection(nameof(ApiFactoryCollection))]
public sealed class ProblemDetailsContractTests(ApiFactory factory)
{
    [Fact]
    public async Task AValidationFailure_CarriesTheFieldsTheFrontendReads()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders", new { Sku = "SKU-PROBLEM", Quantity = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.TryGetProperty("detail", out var detail).Should().BeTrue(
            "ApiError uses `detail` as its message, falling back to `title` only when absent");
        detail.GetString().Should().NotBeNullOrWhiteSpace();

        root.TryGetProperty("traceId", out var traceId).Should().BeTrue(
            "an operator holding a problem report needs it to find the matching log line");
        traceId.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ANotFound_CarriesTheSameShape()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
        root.TryGetProperty("traceId", out _).Should().BeTrue(
            "every ProblemDetails from ResultExtensions.Problem carries one, not just 400s");
    }
}
```

- [ ] **Step 2: Run the tests**

```bash
docker compose up -d --wait
dotnet test tests/Api.IntegrationTests -c Debug --filter "FullyQualifiedName~ProblemDetailsContractTests"
```

Expected: **both pass immediately.** These are characterisation tests over behaviour that already exists — they are red only if the contract is already broken.

If either fails, stop: the hand-written `ProblemDetails` in `client.ts` is already out of step with the API, which is a live bug and worth reporting before continuing.

- [ ] **Step 3: Commit**

```bash
git add tests/Api.IntegrationTests/Orders/ProblemDetailsContractTests.cs
git commit -m "test(api): pin the ProblemDetails fields the frontend reads"
```

---

### Task 5: Generate the frontend types and wire them in

**Files:**
- Modify: `frontend/package.json`
- Create: `frontend/src/api/schema.d.ts` (generated, committed)
- Modify: `frontend/src/features/orders/types.ts`
- Modify: `frontend/src/test/handlers.ts`
- Modify: `frontend/CLAUDE.md`

**Interfaces:**
- Consumes: `openapi/AiFramework.Api.json` from Task 3, and the schema names confirmed in Task 3 Step 4.
- Produces: `frontend/src/api/schema.d.ts` exporting a `components` type. `types.ts` continues to export `Order` and `OrderPage` with the same names, so no component or test import changes.

- [ ] **Step 1: Add the generator**

```bash
npm install --prefix frontend --save-dev --save-exact openapi-typescript@7.13.0
```

`--save-exact` because the repo pins versions deliberately. Confirm it landed in `devDependencies`, not `dependencies`:

```bash
node -e "const p=require('./frontend/package.json');console.log('dev:',p.devDependencies['openapi-typescript'],'| runtime:',p.dependencies['openapi-typescript'])"
```

Expected: `dev: 7.13.0 | runtime: undefined`.

- [ ] **Step 2: Add the script**

In `frontend/package.json`, add to `"scripts"`, after `"format"`:

```json
    "generate:api": "openapi-typescript ../openapi/AiFramework.Api.json --immutable --output src/api/schema.d.ts",
```

`--immutable` is load-bearing: it emits `readonly` members, which is what the hand-written types being replaced already are and what `exactOptionalPropertyTypes` needs.

- [ ] **Step 3: Generate**

```bash
npm run generate:api --prefix frontend
head -40 frontend/src/api/schema.d.ts
```

Expected: a `components` interface containing `schemas` with the names confirmed in Task 3 Step 4, and `readonly` on the members.

- [ ] **Step 4: Replace the hand-written types with aliases**

Replace the whole of `frontend/src/features/orders/types.ts`:

```typescript
import type { components } from '../../api/schema';

// Aliases over the generated schema rather than hand-written interfaces. Components and tests
// keep importing `Order` and `OrderPage` from here, so nothing else churns - but the alias
// stops compiling the moment the backend renames or drops a property, which is the entire
// point. Before this, types.ts restated OrderDtos.cs from memory and nothing connected them.
export type Order = components['schemas']['OrderResponse'];

export type OrderListItem = components['schemas']['OrderListItemResponse'];

export type OrderPage = components['schemas']['OrderPageResponse'];
```

If Task 3 Step 4 recorded different schema names, use those instead.

- [ ] **Step 5: Type the MSW fixture against the schema**

The hand-written fixture in `frontend/src/test/handlers.ts` is where drift hides today: it was written from the same understanding as `types.ts`, so a backend rename leaves both wrong and the tests green. Give it a type.

In `frontend/src/test/handlers.ts`, change the import block and the `anOrder` declaration:

```typescript
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import type { Order } from '../features/orders/types';

// Typed, not loose: an untyped fixture is how a renamed backend property leaves the frontend
// tests passing while the app breaks. This makes the schema fail the build instead.
export const anOrder: Order = {
  id: '11111111-1111-1111-1111-111111111111',
  sku: 'SKU-1',
  quantity: 2,
  placedAt: '2026-09-02T10:00:00+00:00',
};
```

Leave the `handlers` and `server` exports unchanged.

- [ ] **Step 6: Typecheck, lint and test**

```bash
npm run build --prefix frontend
npm run lint --prefix frontend
npm test --prefix frontend -- --run
```

Expected: all pass with no changes to any component or test file.

If the build fails on `exactOptionalPropertyTypes` or on `nextCursor`, that is the risk the spec named. Do **not** loosen `tsconfig`. Adapt in `types.ts` — for example, if the generator emits `nextCursor?: string | null` where the code needs `string | null`, write the alias to reconcile it explicitly and comment why.

- [ ] **Step 7: Document it**

In `frontend/CLAUDE.md`, append:

```markdown
## The API contract

`src/api/schema.d.ts` is **generated** from `openapi/AiFramework.Api.json` at the repo root — do not edit it.
`features/orders/types.ts` is a thin set of aliases over it, which is why a backend rename
breaks the frontend build instead of breaking it at runtime.

Regenerate after any backend contract change:

```bash
npm run generate:api
```

CI regenerates and fails on a diff, so a stale `schema.d.ts` cannot merge.
```

- [ ] **Step 8: Prove it actually catches drift**

This is the only step that tests the feature rather than the plumbing. Temporarily rename a property in the backend and confirm the frontend build breaks:

```bash
sed -i 's/public required string Sku { get; init; }/public required string ProductCode { get; init; }/' src/Api/Orders/OrderDtos.cs
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
npm run build --prefix frontend
```

Expected: the frontend build **fails**, because `handlers.ts` and the order components reference `sku`, which no longer exists.

Before this change, that rename would have left the frontend compiling and its tests green. Revert:

```bash
git checkout -- src/Api/Orders/OrderDtos.cs
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
git diff --stat
```

Expected: no diff — the regenerated document and schema match what is committed.

- [ ] **Step 9: Commit**

```bash
git add frontend/package.json frontend/package-lock.json frontend/src/api/schema.d.ts frontend/src/features/orders/types.ts frontend/src/test/handlers.ts frontend/CLAUDE.md
git commit -m "feat(frontend): generate API types from the OpenAPI document"
```

---

### Task 6: Fail CI when either generated artifact is stale

**Files:**
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: `openapi/AiFramework.Api.json` (Task 3) and `frontend/src/api/schema.d.ts` (Task 5).
- Produces: a CI job named `api contract is current`.

- [ ] **Step 1: Add the job**

In `.github/workflows/ci.yml`, add after the existing `codegen` job and before `frontend`:

```yaml
  contract:
    # The same guard as the `codegen` job above, for the other pair of committed generated
    # artifacts. A contract change that regenerates neither leaves the React app compiling
    # against a shape the API no longer returns - which is the failure this whole feature
    # exists to make impossible.
    name: api contract is current
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}

      - uses: actions/setup-node@v4
        with:
          node-version: ${{ env.NODE_VERSION }}
          cache: npm
          cache-dependency-path: frontend/package-lock.json

      - name: Cache NuGet packages
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj') }}
          restore-keys: nuget-${{ runner.os }}-

      - run: npm ci --prefix frontend

      # BOTH variables are required, and Task 1 proved why. The generator runs the application:
      # without a connection string it dies on Program.cs's startup guard ("ConnectionStrings:
      # Default is not configured"), and with one but still durable, Wolverine's startup
      # migration dials Postgres (ADR 0005). There is no database in this job. The connection
      # string is never opened - it only has to be non-empty.
      - name: Regenerate the OpenAPI document
        env:
          ConnectionStrings__Default: 'Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y'
          Wolverine__Durable: 'false'
        run: dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"

      - name: Regenerate the TypeScript schema
        run: npm run generate:api --prefix frontend

      - name: Fail if either artifact is stale
        run: |
          if ! git diff --exit-code -- openapi/ frontend/src/api/schema.d.ts; then
            echo "::error::The API contract is out of date. Regenerate it with the two commands under 'The API contract' in CLAUDE.md, then commit the result."
            exit 1
          fi
```

- [ ] **Step 2: Validate the YAML parses**

```bash
cd frontend && node -e "
const yaml=require('js-yaml'),fs=require('fs');
const d=yaml.load(fs.readFileSync('../.github/workflows/ci.yml','utf8'));
console.log('jobs:',Object.keys(d.jobs).join(', '));
console.log('contract steps:',d.jobs.contract.steps.length);
"
```

Expected: `contract` appears in the job list with 8 steps.

- [ ] **Step 3: Prove the drift check can fail**

A guard that cannot fail is worthless — the same check applied to the Wolverine codegen job.

```bash
printf '\n' >> frontend/src/api/schema.d.ts
git diff --quiet -- openapi/ frontend/src/api/schema.d.ts && echo "WOULD PASS (wrong)" || echo "WOULD FAIL (correct)"
git checkout -- frontend/src/api/schema.d.ts
```

Expected: `WOULD FAIL (correct)`, then a clean tree.

- [ ] **Step 4: Commit and push, then watch CI**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: fail when the OpenAPI document or generated types are stale"
git push -u origin feat/openapi-and-generated-client
```

Then watch the run and confirm all six jobs pass:

```bash
gh run watch "$(gh run list --branch feat/openapi-and-generated-client --limit 1 --json databaseId --jq '.[0].databaseId')" --exit-status
```

Expected jobs: `backend (Debug)`, `backend (Release)`, `generated code is current`, `api contract is current`, `frontend`, `e2e`.

---

### Task 7: Final verification and pull request

**Files:** none changed.

- [ ] **Step 1: Full local verification, both configurations**

```bash
docker compose up -d --wait
dotnet build -c Debug && dotnet test -c Debug --no-build
dotnet build -c Release && dotnet test -c Release --no-build
npm run lint --prefix frontend && npm run build --prefix frontend && npm test --prefix frontend -- --run
```

Expected: 0 warnings both configurations, 121/121 backend (116 existing + 3 from Task 2 + 2 from Task 4), frontend green.

- [ ] **Step 2: Confirm the publish size still holds**

```bash
dotnet publish src/Api -c Release -o /tmp/final-publish && du -sm /tmp/final-publish
```

Expected: **~19MB**, per Task 3 Step 5 — 2MB above ADR 0005's figure, all of it Scalar and the OpenAPI libraries.

- [ ] **Step 3: Open the pull request**

```bash
gh pr create --base main --head feat/openapi-and-generated-client \
  --title "feat: OpenAPI document, Scalar UI, and a generated frontend client" \
  --body "$(cat <<'EOF'
Implements `docs/superpowers/specs/2026-09-05-openapi-and-generated-client-design.md`.

## What this fixes

`frontend/src/features/orders/types.ts` restated the backend DTOs by hand, and nothing
connected the two. A renamed property left the React app compiling and its tests passing —
because the MSW fixtures were written from the same stale understanding — and broke at runtime.

Task 5 Step 8 demonstrates this: renaming `Sku` now fails the frontend build.

## What is here

- `/openapi/v1.json` and a Scalar UI at `/scalar/v1`, **Development only**, asserted in both
  directions.
- `openapi/AiFramework.Api.json` emitted at build time and committed.
- `frontend/src/api/schema.d.ts` generated from it — types only, no runtime dependency.
- A CI job failing on stale artifacts, in the same shape as the Wolverine codegen guard.
- A test pinning the `ProblemDetails` fields the frontend reads, which OpenAPI cannot describe
  because they live in `Extensions`.

## Verification

121/121 in Debug and Release, 0 warnings. Publish size unchanged at ~17MB.
EOF
)"
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| 1. Document and UI | Task 2 |
| 2. Build-time document, startup problem | Task 1 (probe), Task 3 |
| 3. Generated TypeScript types | Task 5 |
| 4. Error contract | Task 4 |
| 5. Drift guarding | Task 6 |
| 6. Testing | Tasks 2, 4, 5 Step 8, 7 |
| Risks: publish size | Task 3 Step 5, Task 7 Step 2 |
| Risks: `--immutable` vs `exactOptionalPropertyTypes` | Task 5 Step 6 |

The spec's open item — whether asserting the non-Development case needs a second host — is resolved: `WebApplicationFactory.WithWebHostBuilder` plus `UseEnvironment("Production")` does it in the same fixture, so Task 2 asserts it unconditionally.

**Type consistency:** `Order`, `OrderListItem` and `OrderPage` are defined once in Task 5 Step 4 and used in Step 5. Schema names are confirmed in Task 3 Step 4 before Task 5 indexes into them. `anOrder` keeps its name and export.

**Test counts:** 116 existing → 119 after Task 2 → 121 after Task 4. Used consistently in Tasks 2, 3 and 7.
