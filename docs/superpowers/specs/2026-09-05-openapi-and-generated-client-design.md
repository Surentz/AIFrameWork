# OpenAPI: a browsable API and a generated frontend client

**Date:** 2026-09-05
**Status:** Approved, not yet implemented
**Related:** ADR 0005 (Wolverine), `2026-09-02-react-ui-design.md`

## Why

The backend has no way to see or try its own endpoints. `OrdersController` is three actions
and the only way to exercise them is `curl` or the React app.

Separately, and more importantly, the React app's view of the contract is hand-maintained.
`frontend/src/features/orders/types.ts` restates the shape of `OrderResponse` and
`OrderPageResponse` in TypeScript, by hand. Nothing connects the two. Rename a property in
`OrderDtos.cs` and the frontend keeps compiling, keeps passing its tests — because the MSW
handlers are hand-written from the same stale understanding — and breaks at runtime.

That is the real problem. The browsable UI is worth having; the generated client is worth
building.

One piece of groundwork already exists. `Directory.Build.props` carries:

```xml
<!-- Swagger needs the XML doc file; CS1591 (missing doc comment) fires on
     every public member and would stall development under warnings-as-errors. -->
<GenerateDocumentationFile>true</GenerateDocumentationFile>
<NoWarn>$(NoWarn);CS1591</NoWarn>
```

The XML documentation file has been generated since the repo was scaffolded, for exactly
this, and never used.

## Decisions

| Decision | Choice | Rejected |
|---|---|---|
| Document generator | Built-in `Microsoft.AspNetCore.OpenApi` | Swashbuckle, NSwag |
| UI | Scalar | Swagger UI, Redoc |
| Exposure | Development only | Document always; both always |
| Frontend client | Generated **types only** (`openapi-typescript`) | `openapi-fetch` runtime client |
| Document delivery | Build-time artifact, committed | Fetched from a running API |
| Error contract | Stays hand-written, guarded by a test | Generated |

### Versions, verified 2026-09-05 against nuget.org and registry.npmjs.org

| Package | Version |
|---|---|
| `Microsoft.AspNetCore.OpenApi` | 10.0.11 |
| `Microsoft.Extensions.ApiDescription.Server` | 10.0.11 |
| `Scalar.AspNetCore` | 2.17.2 |
| `openapi-typescript` | 7.13.0 |

The two Microsoft packages are on 10.0.11, the same servicing line as this repo's EF Core and
Npgsql pins. 11.0.0 exists only as previews and is not used.

## Non-goals

- **`openapi-fetch`.** It would type paths and verbs as well as payloads, catching a changed
  URL that this design misses. It also means a runtime dependency and rebuilding `ApiError` on
  its error channel. Three endpoints do not justify that yet; revisit if path drift ever
  actually bites.
- **API versioning.** One unversioned surface, no external consumers. Adding version
  negotiation now solves a problem this project does not have.
- **Generating MSW handlers or mock data.** Out of scope; the tests keep their hand-written
  fixtures, now typed against the generated schema.
- **Publishing the document anywhere.** No gateway, no portal, no npm package.

## 1. The document and the UI

`AddOpenApi()` in the Api service registrations, and `MapOpenApi()` plus
`MapScalarApiReference()` behind a single `IsDevelopment()` check in `Program.cs`. A deployed
instance serves neither.

XML `<summary>` comments go on the three `OrdersController` actions. .NET 10's OpenAPI
generator reads them from the already-generated documentation file, and they are what make
Scalar readable rather than a bare list of routes.

The existing `[ProducesResponseType<T>]` annotations are already accurate and carry the
generic payload type, so the document describes real response shapes without further work.

## 2. Build-time document generation

`Microsoft.Extensions.ApiDescription.Server` emits the document to `openapi/v1.json` at the
repository root. The file is committed: it makes a contract change visible as a diff in
review, and it is what the frontend generator reads without needing a running API.

### The startup problem

**This is the highest-risk part of this design and the reason it is called out here rather
than discovered during implementation.**

`ApiDescription.Server` generates the document by *running the application* and asking the
built host for its API descriptions. Since ADR 0005, durable Wolverine migrates its envelope
schema during host startup, which opens a PostgreSQL connection. A build that generates the
document therefore tries to reach a database — and fails where there isn't one, CI above all.

The mitigation is a lever this repo already has: run the generation step with
`Wolverine__Durable=false`, which puts Wolverine in `DurabilityMode.MediatorOnly` and stops it
connecting at all. `HealthTests` and the `codegen` CI job already depend on that same switch,
so this introduces no new concept.

**What is not yet verified is how that value reaches the generator process**, which MSBuild
spawns rather than this repo invoking directly. Implementation starts with a throwaway probe
of exactly that question. In preference order:

1. The environment variable propagates from the build to the spawned process, and a plain
   `dotnet build` works.
2. Generation moves off every-build onto an explicit target or flag, invoked by something that
   owns its environment.
3. Generation is driven by a script rather than MSBuild.

If none is clean, that is a genuine finding: it argues against committing the document at all,
and this design should be revisited rather than forced.

## 3. Generated TypeScript types

`openapi-typescript` as a frontend devDependency, run by an `npm run generate:api` script,
emitting `frontend/src/api/schema.d.ts`. Types only — the package emits no runtime code, so the
frontend gains no runtime dependency.

It runs with `--immutable`, so the output is `readonly`. That is not cosmetic: the existing
hand-written types are `readonly` throughout, and `tsconfig` sets `strict`,
`noUncheckedIndexedAccess` and `exactOptionalPropertyTypes`. Mutable generated types would
either fail to compile against existing call sites or quietly weaken them.

`orders.ts` sources `Order` and `OrderPage` from the generated schema.
`features/orders/types.ts` collapses to thin aliases over the generated types, so components
and tests that import from it do not all churn — and the aliases stop compiling if the
underlying schema drops or renames a property, which is the point.

`client.ts` is not touched.

## 4. The error contract

`ResultExtensions.Problem` puts `errors` and `traceId` into `ProblemDetails.Extensions`, not
into typed properties. OpenAPI will not name them, so generation cannot own the error shape,
and `client.ts` keeps its local `ProblemDetails` interface and its `ApiError`.

That seam is fine, but today it is unguarded: nothing checks that the hand-written interface
still matches what the API actually returns, and `ApiError.fieldErrors` depends on `errors`
existing. A new API integration test asserts a real 400 response body carries `title`,
`detail`, `errors` and `traceId`.

This is the one place in the design where drift would otherwise stay silent, so it gets a real
test rather than a type.

## 5. Drift guarding

A CI job in the same shape as the existing `generated code is current`: regenerate the
document and the types, `git diff --exit-code`, and fail naming the regenerate command.

Two committed generated artifacts now exist in this repo — Wolverine's handler adapters and
these — and they are guarded the same way on purpose. One idea applied twice, not two.

## 6. Testing

The existing 116 tests stay green; none of this changes runtime behaviour on any served path.

Added:

- **ProblemDetails shape** (section 4). An integration test against a real 400.
- **The document is served in Development.** `/openapi/v1.json` returns 200. The dev-only
  decision is a security property, so it is asserted rather than assumed.
- **The document is not served outside Development**, if it can be done without standing up a
  second host. To be confirmed during implementation; if it needs a second `WebApplicationFactory`
  configuration, weigh that cost rather than paying it automatically.

`frontend` keeps its existing tests. They should compile unchanged against the aliases in
section 3 — if they do not, the aliases are wrong.

## Risks

| Risk | Mitigation |
|---|---|
| Build-time generation cannot get `Wolverine__Durable=false` | Probe first; three fallbacks in section 2; abandon the committed document if all fail |
| `--immutable` output still fights `exactOptionalPropertyTypes` | Found immediately — the frontend build fails. Fall back to aliases that adapt the generated shape |
| The generated document changes shape between package versions, churning the committed file | Versions pinned; the drift job makes any change visible in review |
| Scalar adds a transitive dependency surface to the Api project | Development-only middleware, but it ships in the assembly. Check the publish size delta, as was done for Roslyn (17MB → 50MB) |

## Open questions

None blocking. The `Wolverine__Durable` propagation question in section 2 is explicitly the
first implementation step rather than an open design question — the design states what happens
in each outcome.
