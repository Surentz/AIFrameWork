# AIFrameWork

[![CI](https://github.com/Surentz/AIFrameWork/actions/workflows/ci.yml/badge.svg)](https://github.com/Surentz/AIFrameWork/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

A .NET 10 and React 19 reference application built on Clean Architecture, where the
architectural rules are enforced by tooling rather than by convention.

The domain is deliberately small — users place orders against a catalogue — so that the
interesting part is the scaffolding around it: a hook that blocks a layering violation at edit
time, a transactional outbox, a durable event path on PostgreSQL with no broker, background jobs
in a worker host of their own, an API contract generated from the application and committed to
the repository, and a local Kubernetes cluster that runs the whole stack at two replicas to
rehearse the failures that only appear above one.

## Table of contents

- [Background](#background)
- [Architecture](#architecture)
- [Tech stack](#tech-stack)
- [Repository layout](#repository-layout)
- [Install](#install)
- [Usage](#usage)
  - [Running locally](#running-locally)
  - [Running on Kubernetes](#running-on-kubernetes)
  - [Ports](#ports)
- [API](#api)
- [Frontend application](#frontend-application)
- [Generated code](#generated-code)
- [Testing](#testing)
- [Continuous integration](#continuous-integration)
- [Conventions](#conventions)
- [Architecture decision records](#architecture-decision-records)
- [Troubleshooting](#troubleshooting)
- [Contributing](#contributing)
- [License](#license)

## Background

Most Clean Architecture samples describe their dependency rule in prose and then rely on review
to uphold it. This repository takes the position that a rule nobody can enforce is a rule that
erodes, so the dependency rule is a pre-edit hook that refuses the write, the analyzers run as
errors rather than warnings, and the generated artifacts that couple the two stacks together are
committed and re-checked by CI.

The design rationale is recorded in [`docs/adr/`](docs/adr/) and in the specs under
[`docs/superpowers/specs/`](docs/superpowers/specs/). Working notes for contributors and agents
live in [`CLAUDE.md`](CLAUDE.md) and in a per-layer `CLAUDE.md` inside each project.

## Architecture

Five projects: three inner layers, and **two composition roots over them**. Dependencies point
inward only.

```
     ┌─────────────────────────────┐   ┌─────────────────────────────┐
     │             Api             │   │           Worker            │
     │  Controllers, DTOs, hub,    │   │  Job handlers, scheduler,   │
     │  exception handler          │   │  outbox pumps               │
     └──────┬───────────────┬──────┘   └──────┬───────────────┬──────┘
            │               │ DI only         │ DI only       │
            │               ▼                 ▼               │
            │        ┌──────────────────────────────┐         │
            │        │        Infrastructure        │         │
            │        │  EF Core, repositories,      │         │
            │        │  outbox, cache, Wolverine    │         │
            │        └──────────────┬───────────────┘         │
            ▼                       ▼                         ▼
     ┌───────────────────────────────────────────────────────────────┐
     │                          Application                          │
     │           Use cases, ports, Result<T>, validators             │
     └───────────────────────────────┬───────────────────────────────┘
                                     ▼
     ┌───────────────────────────────────────────────────────────────┐
     │                            Domain                             │
     │          Entities, value objects, domain events               │
     └───────────────────────────────────────────────────────────────┘
```

| From ↓ / To → | Domain | Application | Infrastructure | Api | Worker |
| --- | --- | --- | --- | --- | --- |
| **Domain** | — | ✗ | ✗ | ✗ | ✗ |
| **Application** | ✓ | — | ✗ | ✗ | ✗ |
| **Infrastructure** | ✓ | ✓ | — | ✗ | ✗ |
| **Api** | ✓ | ✓ | ✓ DI only | — | ✗ |
| **Worker** | ✓ | ✓ | ✓ DI only | ✗ | — |

**`Api` and `Worker` are siblings, not layers**, and neither may reference the other (ADR 0016).
`Worker → Api` is blocked for a concrete reason beyond tidiness: it is exactly what would let
the two share one Wolverine generated-code tree, and keeping them separate is what makes each
host's `codegen write` independently correct.

`.claude/hooks/dependency-rule.ps1` blocks an edit that would violate any cell except
`Api → Infrastructure` and `Worker → Infrastructure`, which cannot be distinguished mechanically
from a legitimate `services.AddScoped<>()` registration and are carried by review instead.
`Domain` additionally may not reference EF Core, ASP.NET Core, the DI abstractions,
`System.Data`, or `System.ComponentModel.DataAnnotations`.

Two caveats are worth knowing before trusting the ✗ marks in that table:

> **The hooks are PowerShell, and they are invoked as `powershell.exe`.** On Linux or macOS
> without PowerShell installed, none of them run — the dependency rule, the secrets guard, and
> the migration guard are all silently inert. Architecture tests in `Domain.Tests`,
> `Application.Tests`, and `Infrastructure.Tests` assert the inward-pointing rules from inside
> the suite and run everywhere, but they do not currently cover the `Api` or `Worker` rows, so
> on a non-Windows machine those two are carried by review alone.

> **Three cells are unenforced by accident**, and are tracked in [`CLAUDE.md`](CLAUDE.md):
> the hook's banned-namespace table lists `Worker` only under `Api`, so `Domain → Worker`,
> `Application → Worker`, and `Infrastructure → Worker` pass it. Nothing in the repository
> violates them today. The hook also matches `using` directives only, so a fully-qualified
> inline reference is invisible to it, as is a `<ProjectReference>` in a `.csproj`.

Four mechanisms are worth knowing about before reading the code:

- **In-process messaging without MediatR.** `ICommandHandler<TCommand, TResponse>` and
  `IQueryHandler<TQuery, TResponse>` are dispatched through `ICommandDispatcher` and
  `IQueryDispatcher`, with cross-cutting concerns applied as pipeline behaviors. Expected
  failures return a failed `Result<T>` rather than throwing. See ADR 0003.
- **A transactional outbox.** Domain events raised by an aggregate are captured by an EF Core
  interceptor and written in the same transaction as the aggregate itself, then pumped out by a
  background service. A durable Wolverine event path runs alongside it on PostgreSQL, and
  publishes integration events for other systems to RabbitMQ. See ADR 0005 and ADR 0026.
- **Opt-in query caching, scoped to the caller.** A query implements `ICacheable`, a command
  implements `IInvalidatesCache`, and the behavior composes the cache key from the query type
  and the current user's id. Nothing on the authentication path is cached. See ADR 0009.
- **Background jobs in a worker host of their own.** A job is a message with a lane
  (`Light`/`Heavy`, one RabbitMQ quorum queue each), enqueued through `IJobScheduler`. **The API
  publishes and listens to nothing**; the worker is the only host with listeners, which is
  asserted from the runtime's own endpoint list rather than intended. Scheduled jobs use Quartz
  as the clock and fire on exactly one worker. See ADR 0016 and ADR 0017.

## Tech stack

### Backend

| | |
| --- | --- |
| Runtime | .NET 10 (`net10.0`), C# 14, SDK 10.0.400 |
| Web | ASP.NET Core, controller-based |
| Persistence | PostgreSQL 17 via EF Core 10 and Npgsql |
| Messaging | Hand-rolled dispatchers; WolverineFx 6 for the durable event path, jobs and integration events; RabbitMQ 4 as the broker |
| Jobs | Worker host over RabbitMQ quorum queues; Quartz.NET 4 as the scheduler clock |
| Caching | `HybridCache` (L1 only today) |
| Realtime | SignalR, opt-in, with a Redis backplane above one replica |
| Validation | FluentValidation |
| Auth | Cookie session with a rotating security stamp; Data Protection key ring in PostgreSQL |
| Resilience | `Microsoft.Extensions.Http.Resilience` outbound; `EnableRetryOnFailure` on Npgsql |
| Observability | `Microsoft.Extensions.Logging` + OpenTelemetry over OTLP; Seq locally, OpenSearch in-cluster |
| API docs | `Microsoft.AspNetCore.OpenApi` rendered by Scalar |
| Analysis | .NET analyzers, SonarAnalyzer, Meziantou.Analyzer, AsyncFixer — all as errors |

### Frontend

| | |
| --- | --- |
| Framework | React 19 with TypeScript 6 |
| Build | Vite 8 |
| Routing | React Router 7 |
| Server state | TanStack Query 5 |
| API types | Generated from the committed OpenAPI document |
| Styling | Plain CSS with design tokens |
| Lint/format | ESLint (`--max-warnings 0`) and Prettier |

### Testing and infrastructure

| | |
| --- | --- |
| Backend tests | xUnit, FluentAssertions, NSubstitute, Testcontainers |
| Frontend tests | Vitest, React Testing Library, MSW |
| End-to-end | Playwright against a built preview bundle |
| Containers | Docker Compose for development, chiselled images for deployment |
| Orchestration | kind with Kustomize overlays and ingress-nginx |
| CI | GitHub Actions, Debug and Release |

Exact pins live in [`CLAUDE.md`](CLAUDE.md), `Directory.Build.props`, the `.csproj` files, and
`frontend/package.json`. CI pins the SDK and Node versions in `.github/workflows/ci.yml`.

## Repository layout

```
.
├── src/
│   ├── Domain/            Entities, value objects, domain events, domain exceptions
│   ├── Application/       Use cases, ports, Result<T>, validators
│   ├── Infrastructure/    EF Core, repositories, outbox, caching, Wolverine, jobs, security
│   ├── Api/               Controllers, DTOs, the SignalR hub, exception handling, composition root
│   │   └── Internal/Generated/    Wolverine adapters for the event path — committed
│   └── Worker/            The job host: handlers, Quartz scheduling, the outbox pumps
│       └── Internal/Generated/    Wolverine adapters for the jobs — committed, and its own
├── tests/
│   ├── Domain.Tests/
│   ├── Application.Tests/
│   ├── Infrastructure.Tests/     Testcontainers-backed PostgreSQL fixtures
│   ├── Api.IntegrationTests/     WebApplicationFactory over the real pipeline
│   └── Worker.IntegrationTests/  Job delivery, scheduling, and the worker's codegen
├── frontend/              Vite + React workspace, Vitest specs, Playwright e2e
├── k8s/
│   ├── base/              api, web, worker, postgres, redis, ingress, autoscaling
│   ├── components/        observability — the OTel Collector and OpenSearch, opt-in
│   └── overlays/          local, and local-observability
├── deploy/                kind cluster definition and deployment scripts
├── scripts/               Development loop and prerequisite scripts
├── local-run/             control-panel.bat, a double-clickable menu
├── openapi/               The committed API contract
├── docs/
│   ├── adr/               Architecture decision records
│   ├── local-development.md
│   └── superpowers/       Specs and implementation plans
└── .claude/               Hooks, agents, skills, and slash commands
```

## Install

### Prerequisites

- Windows 11 with PowerShell (the helper scripts are PowerShell; everything else is portable)
- .NET SDK 10
- Node.js 24.15 or newer (24.20.0 is what CI pins)
- Docker Desktop
- `kubectl`, `kind`, and optionally `k9s`, for the Kubernetes path only

On a new machine you do not have to install these by hand:

```powershell
./scripts/install-prereqs.ps1
```

It checks for each tool and installs whatever is missing through `winget`. It only installs what
is entirely absent; a tool that is present but older than expected is reported rather than
silently upgraded, because upgrading Docker Desktop or Node.js affects every other project on
the machine. Docker Desktop's own first run (WSL2 backend, licence terms, a restart) cannot be
scripted and the script says so when it reaches that point.

The same script is option 1 of [`local-run/control-panel.bat`](local-run/control-panel.bat), a
double-clickable menu that wraps every script in this section.

### Clone and restore

```bash
git clone https://github.com/Surentz/AIFrameWork.git
cd AIFrameWork
dotnet tool restore        # dotnet-ef, from .config/dotnet-tools.json
npm install --prefix frontend
```

`scripts/dev.ps1` runs `npm install` by itself when `frontend/node_modules` is missing, so a
first run needs no separate step.

## Usage

### Running locally

One command starts PostgreSQL and RabbitMQ, applies migrations, and launches the API, the **job worker**, and
the Vite dev server in windows of their own:

```powershell
./scripts/dev.ps1              # three windows: API (5234), worker (5235), Vite (5173)
./scripts/dev.ps1 -WithSeq     # same, plus Seq for structured logs at localhost:55341
./scripts/worker.ps1           # just the worker, in this window — for restarting it alone
```

To stop all of it:

```powershell
./scripts/stop-dev.ps1
```

The worker is not optional scenery: jobs run there and **the API listens to nothing**, so
without that window an enqueued job simply sits on its RabbitMQ queue and nothing says so.
`worker.ps1` exists because `codegen write` requires a worker restart before new adapters take
effect, and restarting it otherwise means stopping everything.

By hand, if you want the pieces separately:

```bash
docker compose up -d --wait
dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
dotnet run --project src/Api
dotnet run --project src/Worker
npm start --prefix frontend
```

The app opens at <http://localhost:5173> and the API reference at
<http://localhost:5234/scalar/v1>. Both open a browser tab of their own. Vite proxies `/api` to
the backend, which is what lets the session cookie be same-origin.

Further detail, including one-keystroke startup in Rider and Visual Studio, is in
[`docs/local-development.md`](docs/local-development.md).

### Running on Kubernetes

A local kind cluster that runs the stack at **two API replicas across two worker nodes**, to
rehearse what only breaks above one: a shared Data Protection key ring, cache eviction under
ingress cookie affinity, and a realtime push that has to reach a user whose connection is held
by the other pod. Docker Compose remains the inner development loop; this is additive.

```powershell
./deploy/start-cluster.ps1     # creates the cluster if missing, otherwise redeploys
./deploy/deploy.ps1            # rebuild, migrate, roll out onto an existing cluster
./deploy/deploy.ps1 -WithObservability   # same, plus the OTel Collector -> OpenSearch stack
./deploy/teardown.ps1          # deletes the cluster, and the data inside it
```

Two replicas means two *failure domains*, not two processes: `deploy/kind-cluster.yaml` declares
a control-plane node plus two workers, and `api` spreads across them with
`whenUnsatisfiable: DoNotSchedule`. HPAs cover `api` and `web` with `minReplicas: 2` as a floor,
and all three workloads have PodDisruptionBudgets. The **worker has a budget but no autoscaler**
— its work arrives through queues, so queue depth is the signal an autoscaler would need, and
CPU would scale it down exactly when it is blocked and falling behind. See ADR 0018.

`-WithObservability` is opt-in and is by a wide margin the largest thing in the cluster
(OpenSearch wants real JVM heap, Dashboards is a second Node process) — reach for it only when
the logging pipeline itself is what you are rehearsing. It is deliberately **not** part of the
`e2e-k8s.ps1` readiness gate: a log store has no business in the readiness path of an e2e run.

The deployment is three phases in a fixed order, because Kustomize has no hook mechanism:
configuration and PostgreSQL first, then migrations as a Job run to completion, and only then the
Deployments and the Ingress. An API pod therefore never starts against an unmigrated schema.

Open <https://aiframework.localtest.me:8443>. That name resolves to `127.0.0.1` publicly, so
there is nothing to add to your `hosts` file, and the certificate is self-signed so the browser
warns once. The ingress is published on 8080/8443 rather than 80/443 because those ports are
frequently taken on Windows by IIS or BranchCache through `http.sys`.

Four things that will cost you time are documented in [`CLAUDE.md`](CLAUDE.md): TLS is not
optional in Production because the session cookie is `Secure`; configuration keys need double
underscores (`Cache__Enabled`, not `Cache_Enabled` — a single underscore binds nothing and warns
nothing); the migration Job is deleted before being re-applied, because a completed Job has
immutable fields; and **metrics-server is installed patched with `--kubelet-insecure-tls`**,
without which every HPA reports `unknown` and never scales — as a *condition on the HPA*, not as
a deploy failure, so the cluster looks healthy while the autoscalers do nothing.

PostgreSQL is pinned to whichever node it first scheduled on by kind's node-local storage class,
so draining that node will not reschedule it. Target a different node for a drain test.

> The local overlay commits a PostgreSQL password and a self-signed private key on purpose.
> They are throwaway values for a localhost-only cluster that is never deployed, each file says
> so in its own header, and neither is a pattern to copy into an overlay that targets a real
> environment.

### Ports

| Port | What | Source |
| --- | --- | --- |
| 5173 | Vite dev server | `frontend/vite.config.ts` |
| 5234 | API | `src/Api/Properties/launchSettings.json` |
| 5235 | Job worker (health endpoints only) | `src/Worker/Properties/launchSettings.json` |
| 4173 | Vite preview, used by the e2e suite | `frontend/vite.config.ts` |
| 55433 | Development PostgreSQL, data persists | `docker-compose.yml` |
| 55432 | End-to-end PostgreSQL, throwaway | `docker-compose.e2e.yml` |
| 55672/55673 | Development RabbitMQ: AMQP / management UI (`aiframework`/`aiframework`) | `docker-compose.yml` |
| 55682/55683 | End-to-end RabbitMQ, throwaway | `docker-compose.e2e.yml` |
| 55341 | Seq, only with `dev.ps1 -WithSeq` | `docker-compose.yml` (`observability` profile) |
| 8080/8443 | kind ingress | `deploy/kind-cluster.yaml` |

The two databases are meant to coexist. The two *API processes* are not: `npm run e2e` starts its
own API on 5234 with `reuseExistingServer: false`, so stop the development API before an e2e run
or set `API_PORT`.

## API

All endpoints are under `/api`. Authentication is a `HttpOnly`, `SameSite=Lax` session cookie
named `aiframework.session`; `SameSite=Lax` combined with JSON-only request bodies is the CSRF
defence, so no antiforgery token is issued.

| Method | Route | Auth | Purpose |
| --- | --- | --- | --- |
| `POST` | `/api/auth/register` | anonymous, rate limited | Create an account and sign in |
| `POST` | `/api/auth/login` | anonymous, rate limited | Sign in |
| `POST` | `/api/auth/logout` | cookie | Sign out |
| `GET` | `/api/auth/me` | cookie | The current session |
| `POST` | `/api/auth/change-password` | cookie | Change your own password |
| `POST` | `/api/auth/sign-out-everywhere` | cookie | Invalidate every session for this user |
| `POST` | `/api/orders` | cookie | Place an order |
| `GET` | `/api/orders` | cookie | List your orders, paged |
| `GET` | `/api/orders/{id}` | cookie | One of your orders |
| `POST` | `/api/orders/{id}/ship` | cookie | Mark one of your orders shipped; 409 on an illegal transition |
| `POST` | `/api/orders/{id}/cancel` | cookie | Cancel one of your orders; 409 on an illegal transition |
| `POST` | `/api/products` | cookie | Add a product to the catalogue |
| `GET` | `/api/products` | cookie | List the catalogue, paged |
| `GET` | `/api/products/{id}` | cookie | One product |
| `PUT` | `/api/products/{id}` | cookie | Replace a product's editable fields — not the sku |
| `GET` | `/api/notifications` | cookie | Your notification feed, newest first, paged |
| `GET` | `/api/notifications/unread-count` | cookie | Your unread count, for the badge |
| `POST` | `/api/notifications/{id}/read` | cookie | Mark one notification read |
| `POST` | `/api/notifications/read-all` | cookie | Mark every unread notification read |
| `GET` | `/api/rates?from=&to=` | cookie | An exchange rate; 503 once the provider's retry budget is spent |
| `GET` | `/health` | anonymous | Liveness; never touches the database |
| `GET` | `/health/ready` | anonymous | Readiness; checks PostgreSQL |

The worker serves `/health` and `/health/ready` of its own on 5235, and nothing else — it is a
web host only so that Kubernetes has something to probe.

`/hubs/notifications` is a SignalR hub, mapped only when `Realtime__Enabled` is on. It is
**best-effort by contract**: the feed is the truth, and the push only closes the window in which
the badge would otherwise be up to thirty seconds stale. See ADR 0019.

The catalogue is `[Authorize]` rather than anonymous even for reads, and that is mechanical
rather than a product decision: an `ICacheable` query dispatched with no current user throws,
because the cache key is composed with `ICurrentUser.Id` and an unscoped entry would be shared
across every caller.

Orders belong to the user who placed them and are never reassigned (ADR 0007), so a request for
someone else's order is a 404 rather than a 403.

Failures are RFC 9457 `ProblemDetails` throughout, including the rate limiter's 429. Expected
failures never throw: a handler returns a failed `Result`, and `ErrorKind` maps to a status code
(`Validation` → 400, `Unauthorized` → 401, `NotFound` → 404, `Conflict` → 409). Thrown
`DomainException`s become 400 through the global `IExceptionHandler`; anything else is a logged
500 whose message is not leaked.

Credential endpoints are protected twice over (ADR 0008): a fixed-window rate limiter partitioned
by remote address, and a self-expiring per-account lockout after five failed attempts. The
lockout is deliberately silent, so that neither mechanism reveals which accounts exist.

**Sessions can be invalidated after the fact** (ADR 0011). Each user carries a rotating
`SecurityStamp`; the cookie carries the stamp it was issued under, and every authenticated
request compares the two in `OnValidatePrincipal` before the endpoint sees it. Three things
rotate it — a password change, the failed sign-in that locks an account, and
`POST /api/auth/sign-out-everywhere` — and a rotation ends every cookie already issued for that
user. That read is deliberately uncached and always will be: `HybridCache` here is L1-only, so
caching it would let a revoked session survive on another replica for the length of the TTL.
Changing your own password re-issues your own cookie, which is why `ChangePassword` returns a
`SessionView` rather than a bool.

The interactive reference is Scalar at `/scalar/v1`, and it is **Development only** — a deployed
instance must not publish its endpoint surface, and an integration test asserts that in both
directions.

## Frontend application

```
frontend/src/
├── api/            Generated schema, typed client, endpoint wrappers
├── features/
│   ├── auth/           Login, register, change password, RequireAuth guard
│   ├── orders/         List, detail, place-order form, query hooks
│   ├── products/       Catalogue list, detail, create and edit forms
│   └── notifications/  The header bell, the feed, and the SignalR stream
├── components/
├── styles/         tokens.css, global.css, controls.css
├── test/           MSW handlers, setup, query-client helper
└── routes.tsx
```

| Route | Component | Access |
| --- | --- | --- |
| `/login` | `LoginPage` | anonymous, full-bleed |
| `/register` | `RegisterPage` | anonymous, full-bleed |
| `/` | redirect to `/orders` | authenticated |
| `/orders` | `OrderList` | authenticated |
| `/orders/new` | `PlaceOrderForm` | authenticated |
| `/orders/:id` | `OrderDetail` | authenticated |
| `/products` | `ProductList` | authenticated |
| `/products/new` | `CreateProductForm` | authenticated |
| `/products/:id` | `ProductDetail` | authenticated |
| `/products/:id/edit` | `EditProductForm` | authenticated |
| `/notifications` | `NotificationList` | authenticated |
| `/account/password` | `ChangePasswordPage` | authenticated |

`/products/new` is declared before `/products/:id` so that `new` matches the literal route
rather than being captured as an id.

`RequireAuth` wraps the layout rather than the other way round, so a signed-out visitor is
redirected before a header they cannot use is rendered. Server state is TanStack Query; there is
no client state library, because there is no client state worth the dependency.

```bash
npm start   --prefix frontend    # dev server on 5173
npm run build --prefix frontend  # tsc -b && vite build
npm test    --prefix frontend    # vitest (watch); add -- --run for one pass
npm run lint --prefix frontend   # eslint, zero warnings tolerated
npm run e2e --prefix frontend    # Playwright
```

## Generated code

Three artifacts are generated, committed, and re-checked by CI. Each exists because the failure
it prevents is invisible in a normal build.

**The API contract.** `openapi/AiFramework.Api.json` is generated from the running application
and `frontend/src/api/schema.d.ts` from that. After changing a controller, a DTO, or a
`[ProducesResponseType]`:

```bash
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false Admin__ReconcileOnStart=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
```

Generation runs the whole application, which is why both environment variables are required: the
startup guard rejects an empty connection string, and a durable Wolverine dials PostgreSQL. The
connection string is never actually opened. It is an explicit MSBuild target rather than part of
`dotnet build`, so that an ordinary build stays ordinary.

**Wolverine handler adapters — there are TWO trees, not one.** Wolverine builds its adapters
with Roslyn, and Release ships without the compiler because it costs 33MB (measured: a Release
publish is 17MB without it, 50MB with). Release instead loads adapters generated ahead of time.
`TypeLoadMode.Static` resolves them out of each host's own `opts.ApplicationAssembly`, and the
worker cannot share the API's — that would need the `Worker → Api` reference the dependency rule
forbids. After adding or changing a handler, regenerate the tree(s) it belongs to:

```bash
dotnet run --project src/Api -- codegen write      # event-path handlers
dotnet run --project src/Worker -- codegen write   # job handlers, and JobUserMiddleware
```

This is the trap the Release dimension in CI exists for: stale generated code leaves Debug green
and the build succeeding, and fails only in Release, at startup. Debug stays green with *either*
tree stale, and CI checks both.

**CI's Linux output is the authority, and Windows does not always match it.** If a Windows
regeneration reorders statements inside a handler you did not change, keep the committed
version — the two orders do the same thing, each platform is stable, and CI's "generated code is
current" job is what decides.

**EF Core migrations.** Never hand-edit an applied migration; add a new one.
`.claude/hooks/protect-migrations.ps1` enforces this against git history, so a migration you have
just generated and not yet committed is still yours to adjust, while one that is in `HEAD` is
refused even if you delete the file first.

## Testing

```bash
dotnet test                          # five projects, ~95 test files
npm test --prefix frontend -- --run  # Vitest
npm run e2e --prefix frontend        # Playwright
```

Inside Claude Code, `/verify` runs all of the above plus both lint steps **and the two
generated-artifact drift checks**, and reports what actually ran — skipping any toolchain that
is not installed rather than reporting a false pass.

| Suite | What belongs there |
| --- | --- |
| `Domain.Tests` | Invariants and domain events, no infrastructure |
| `Application.Tests` | Handlers against substituted ports; architecture tests |
| `Infrastructure.Tests` | Repositories, outbox, caching, against real PostgreSQL via Testcontainers |
| `Api.IntegrationTests` | The real pipeline through `WebApplicationFactory` |
| `Worker.IntegrationTests` | Job delivery, Quartz scheduling, and the worker's own codegen |
| `frontend/src/**/*.test.tsx` | Components and hooks, with MSW standing in for the API |
| `frontend/e2e/*.spec.ts` | Browser journeys against a built preview bundle |

Testcontainers needs a running Docker daemon. The caching behavior, the rate limiter, and the
resilience pipeline's retries are all disabled by default under test and re-enabled by the
specific suites that exercise them, so that none becomes an intermittent failure — or a source
of backoff delay — in tests that are not about them.

Several suites exist specifically to catch a class of regression that review alone would miss:
`JobRegistrationTests` fails the build on an unregistered job, `ApiPublishesOnlyTests` asserts
against the runtime's own endpoint list that no `aiframework.jobs.*` queue has a listener on the API host,
`SensitiveCommandLoggingTests` catches a logging change that would write plaintext passwords to
the log store, and `WolverineCodegenTests`/`WorkerCodegenTests` catch stale adapters in Debug —
where they would otherwise stay invisible until Release.

## Continuous integration

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push to `main` and every
pull request:

| Job | What it proves |
| --- | --- |
| `backend (Debug)` / `backend (Release)` | Builds and tests in both configurations |
| `generated code is current` | Re-runs `codegen write` for **both** trees and fails on any diff |
| `api contract is current` | Regenerates the OpenAPI document and the TypeScript schema, and fails on any diff |
| `frontend` | Lint, build, and Vitest |
| `e2e` | Playwright, gated behind the fast jobs |

Release is a separate matrix leg rather than an afterthought: Release was broken in this
repository for the whole life of the Wolverine spike without anyone noticing, because
`dotnet build` succeeded with zero warnings and only the startup failed.

The two "is current" jobs fail on a **diff**, not on a build error, which is what lets a working
tree build, test, and lint clean while CI rejects it. `/verify` runs both as its step 3 for
exactly that reason — so the one thing CI does that a local `/verify` does not is the Release
leg.

## Conventions

Non-negotiable, and mostly machine-checked:

- **Warnings are errors**, from the compiler, the analyzers, and the build. Repository-wide
  exemptions live in `.editorconfig`; anything narrower is a local `#pragma warning
  disable`/`restore` pair with a justification comment above it.
- **Nullable is enabled.** A missing null check does not compile.
- **`required` in `Domain`, never `[Required]`.** DataAnnotations belong on Api DTOs.
- **Never `catch (Exception)`.** The global `IExceptionHandler` receives it as a parameter.
  `throw;`, never `throw ex;`. CA1031 is an error everywhere except the outbox's background
  pumps, which have no such parameter and must not die mid-loop.
- **No secrets in `appsettings*.json`.** Use `dotnet user-secrets` or environment variables. A
  pre-edit hook refuses the write (not the commit — nothing here hooks git). The committed
  development connection string and the local Kubernetes overlay are deliberate, documented
  exceptions: throwaway credentials for localhost-only containers that are never deployed.
- **Never hand-edit an applied EF migration.** Add a new one. The hook keys on git history, so a
  migration you have just generated and not yet committed is still yours to adjust, while one
  that is in `HEAD` is refused even if you delete the file first.
- **Handlers do not log their own outcome.** Every command and query is already wrapped by a
  logging behavior that records outcome and duration, and it logs `typeof(TRequest).Name` rather
  than the request instance — `SignIn`, `RegisterUser`, and `ChangePassword` all carry a
  plaintext password field. See ADR 0015.

Per-language detail lives in the `dotnet-conventions`, `dotnet-testing`, `react-conventions`, and
`react-testing` skills; `regenerate` and `jobs` cover the two procedures most likely to be got
wrong — which generated artifact to rebuild after a change, and the job framework's traps. Each
layer has a `CLAUDE.md` of its own.

## Architecture decision records

All accepted, in [`docs/adr/`](docs/adr/).

| # | Decision |
| --- | --- |
| [0001](docs/adr/0001-record-architecture-decisions.md) | Record architecture decisions |
| [0002](docs/adr/0002-clean-architecture-with-enforced-dependency-rule.md) | Clean Architecture with a machine-enforced dependency rule |
| [0003](docs/adr/0003-in-process-messaging-without-mediatr.md) | In-process messaging without MediatR |
| [0004](docs/adr/0004-react-over-angular.md) | React over Angular |
| [0005](docs/adr/0005-wolverine-for-the-event-path.md) | Wolverine for the event path, on PostgreSQL, without a broker |
| [0006](docs/adr/0006-username-and-password-authentication.md) | Username and password authentication, on a session cookie |
| [0007](docs/adr/0007-orders-belong-to-the-user-who-placed-them.md) | Orders belong to the user who placed them |
| [0008](docs/adr/0008-brute-force-protection.md) | Brute-force protection: rate limiting and a self-expiring lockout |
| [0009](docs/adr/0009-caching-scoped-to-the-caller.md) | Query caching: an opt-in pipeline behavior, scoped to the caller |
| [0010](docs/adr/0010-running-on-kubernetes.md) | Running on Kubernetes: shared state across two replicas |
| [0011](docs/adr/0011-session-invalidation-on-a-security-stamp.md) | Session invalidation on a rotating security stamp |
| [0012](docs/adr/0012-end-to-end-test-architecture.md) | End-to-end test architecture |
| [0013](docs/adr/0013-a-global-catalogue-behind-a-caller-scoped-cache.md) | A global catalogue behind a caller-scoped cache |
| [0014](docs/adr/0014-retry-and-resilience-policies.md) | Retry and resilience policies on the request path |
| [0015](docs/adr/0015-centralized-logging-with-opentelemetry.md) | Centralized logging with MEL and OpenTelemetry |
| [0016](docs/adr/0016-jobs-in-a-worker-host.md) | Jobs run in a worker host, with lanes as queues (the transport superseded by 0026) |
| [0017](docs/adr/0017-quartz-as-the-job-clock.md) | Quartz.NET as the job clock |
| [0018](docs/adr/0018-load-balancing-within-the-affinity-constraint.md) | Load balancing within the affinity constraint |
| [0019](docs/adr/0019-realtime-notifications-over-signalr.md) | Realtime notifications over SignalR, with a Redis backplane |
| [0020](docs/adr/0020-an-administrator-role.md) | An administrator role |
| [0021](docs/adr/0021-operational-telemetry-in-postgres.md) | Operational telemetry in Postgres |
| [0022](docs/adr/0022-configuration-seeds-the-administrator-list.md) | Configuration seeds the administrator list, rather than mirroring it |
| [0023](docs/adr/0023-the-e2e-stack-starts-the-job-worker.md) | The e2e stack starts the job worker |
| [0024](docs/adr/0024-capability-policies-and-operator-fulfilment.md) | Capability-named policies, and the operator ships |
| [0025](docs/adr/0025-the-catalogue-is-managed-by-administrators.md) | The catalogue is managed by administrators |
| [0026](docs/adr/0026-rabbitmq-for-asynchronous-work.md) | RabbitMQ is the broker for all asynchronous work |

Record a new one with `/adr <title>`. **Check the open branches as well as `docs/adr/` before
taking a number** — two branches that each take "the next one" produce a duplicate, which has
happened once already and needed a renumbering commit to undo.

## Troubleshooting

**`dotnet ef` cannot see user-secrets.** Migrations run through
`DesignTimeDbContextFactory`, which reads only the `ConnectionStrings__Default` environment
variable, so a `database update` against anything but the default needs it passed explicitly.
The reverse also bites: a user-secret sits above `appsettings.Development.json` in configuration
order, so a stale value there wins at runtime while `dotnet ef` ignores it. Check with
`dotnet user-secrets list --project src/Api` when the app and the migrations disagree about
which database they are using.

**`'vite' is not recognized`.** `frontend/node_modules` is missing. Run
`npm install --prefix frontend`, or just use `scripts/dev.ps1`, which does it for you.

**`Cannot find native binding` from rolldown.** A global `~/.npmrc` pinning `os=` or `cpu=` to
the wrong platform makes npm skip every platform-specific optional dependency silently. Remove
the line, delete `node_modules` and `package-lock.json`, and reinstall.
`scripts/install-prereqs.ps1` flags this.

**The browser drops the session cookie on Kubernetes.** `ASPNETCORE_ENVIRONMENT=Production` sets
`CookieSecurePolicy.Always`, so over plain HTTP login appears to succeed and every later request
is a 401 with nothing in the logs. Use the HTTPS ingress.

**`kind create cluster` fails to bind port 80.** Something is holding it through `http.sys`,
usually IIS or BranchCache. The cluster definition already publishes 8080/8443 instead; if you
changed that back, either free the port from an elevated shell or restore the remap.

**A Vite proxy `ECONNREFUSED` for `/api/...` at startup.** The frontend came up before the API
finished starting. `scripts/dev.ps1` pauses five seconds between the two for exactly this;
reload once and it resolves.

## Contributing

1. Read [`CLAUDE.md`](CLAUDE.md) and the `CLAUDE.md` of the layer you are touching.
2. Scaffold with `/feature <name>`, `/react-feature <name>`, or `/job <Name>` so the layering
   and the tests come out right the first time.
3. Keep the dependency rule intact. The hook will stop you on Windows — but it is a backstop,
   not a design tool, and on Linux or macOS it is not running at all.
4. Regenerate and commit any affected [generated code](#generated-code): the API contract after
   touching a controller, a DTO, or a `[ProducesResponseType]`; the Wolverine tree(s) after
   touching a handler.
5. Run `/verify` before opening a pull request. CI runs the same checks plus the Release
   configuration.
6. Record anything architecturally load-bearing with `/adr <title>`.

Issues and pull requests go to <https://github.com/Surentz/AIFrameWork>.

## License

[Apache License 2.0](LICENSE). The copyright line in `LICENSE` is still the unfilled template
placeholder; set it before publishing this anywhere that matters.
