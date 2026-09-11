# AIFrameWork

[![CI](https://github.com/Surentz/AIFrameWork/actions/workflows/ci.yml/badge.svg)](https://github.com/Surentz/AIFrameWork/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

A .NET 10 and React 19 reference application built on Clean Architecture, where the
architectural rules are enforced by tooling rather than by convention.

The domain is deliberately small — users place orders — so that the interesting part is the
scaffolding around it: a hook that blocks a layering violation at edit time, a transactional
outbox, a durable event path on PostgreSQL with no broker, an API contract generated from the
application and committed to the repository, and a local Kubernetes cluster that runs the whole
stack at two replicas to rehearse the failures that only appear above one.

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

Four projects, dependencies pointing inward only.

```
                ┌─────────────────────────────────────────┐
                │                   Api                   │
                │  Controllers, DTOs, exception handler,  │
                │          composition root               │
                └───────┬──────────────┬──────────────────┘
                        │              │ DI registration only
                        ▼              ▼
        ┌───────────────────────┐   ┌──────────────────────────────┐
        │      Application      │◄──│        Infrastructure        │
        │  Use cases, ports,    │   │  EF Core, repositories,      │
        │  Result<T>, validators│   │  outbox, cache, Wolverine    │
        └───────────┬───────────┘   └──────────────┬───────────────┘
                    │                              │
                    ▼                              ▼
                ┌─────────────────────────────────────────┐
                │                 Domain                  │
                │  Entities, value objects, domain events │
                └─────────────────────────────────────────┘
```

| From ↓ / To → | Domain | Application | Infrastructure | Api |
| --- | --- | --- | --- | --- |
| **Domain** | — | ✗ | ✗ | ✗ |
| **Application** | ✓ | — | ✗ | ✗ |
| **Infrastructure** | ✓ | ✓ | — | ✗ |
| **Api** | ✓ | ✓ | ✓ DI only | — |

`.claude/hooks/dependency-rule.ps1` blocks an edit that would violate any cell except
`Api → Infrastructure`, which cannot be distinguished mechanically from a legitimate
`services.AddScoped<>()` registration and is carried by review instead. `Domain` additionally
may not reference EF Core, ASP.NET Core, the DI abstractions, `System.Data`, or
`System.ComponentModel.DataAnnotations`, and architecture tests assert the same rules from
inside the test suite.

Three mechanisms are worth knowing about before reading the code:

- **In-process messaging without MediatR.** `ICommandHandler<TCommand, TResponse>` and
  `IQueryHandler<TQuery, TResponse>` are dispatched through `ICommandDispatcher` and
  `IQueryDispatcher`, with cross-cutting concerns applied as pipeline behaviors. Expected
  failures return a failed `Result<T>` rather than throwing. See ADR 0003.
- **A transactional outbox.** Domain events raised by an aggregate are captured by an EF Core
  interceptor and written in the same transaction as the aggregate itself, then pumped out by a
  background service. A durable Wolverine event path runs alongside it on PostgreSQL, with no
  message broker. See ADR 0005.
- **Opt-in query caching, scoped to the caller.** A query implements `ICacheable`, a command
  implements `IInvalidatesCache`, and the behavior composes the cache key from the query type
  and the current user's id. Nothing on the authentication path is cached. See ADR 0009.

## Tech stack

### Backend

| | |
| --- | --- |
| Runtime | .NET 10 (`net10.0`), C# 14, SDK 10.0.400 |
| Web | ASP.NET Core, controller-based |
| Persistence | PostgreSQL 17 via EF Core 10 and Npgsql |
| Messaging | Hand-rolled dispatchers; WolverineFx 6 for the durable event path |
| Caching | `HybridCache` (L1 only today) |
| Validation | FluentValidation |
| Auth | Cookie session, Data Protection key ring persisted to PostgreSQL |
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
│   ├── Infrastructure/    EF Core, repositories, outbox, caching, Wolverine, security
│   └── Api/               Controllers, DTOs, exception handling, composition root
├── tests/
│   ├── Domain.Tests/
│   ├── Application.Tests/
│   ├── Infrastructure.Tests/     Testcontainers-backed PostgreSQL fixtures
│   └── Api.IntegrationTests/     WebApplicationFactory over the real pipeline
├── frontend/              Vite + React workspace, Vitest specs, Playwright e2e
├── k8s/                   Kustomize base and the local overlay
├── deploy/                kind cluster definition and deployment scripts
├── scripts/               Development loop and prerequisite scripts
├── local-run/             control-panel.bat, a double-clickable menu
├── openapi/               The committed API contract
├── docs/
│   ├── adr/               Architecture decision records
│   └── superpowers/       Specs and implementation plans
└── .claude/               Hooks, agents, and slash commands
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

One command starts PostgreSQL, applies migrations, and launches the API and the Vite dev server
in windows of their own:

```powershell
./scripts/dev.ps1
```

To stop all three:

```powershell
./scripts/stop-dev.ps1
```

By hand, if you want the pieces separately:

```bash
docker compose up -d --wait
dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
dotnet run --project src/Api
npm start --prefix frontend
```

The app opens at <http://localhost:5173> and the API reference at
<http://localhost:5234/scalar/v1>. Both open a browser tab of their own. Vite proxies `/api` to
the backend, which is what lets the session cookie be same-origin.

Further detail, including one-keystroke startup in Rider and Visual Studio, is in
[`docs/local-development.md`](docs/local-development.md).

### Running on Kubernetes

A local kind cluster that runs the stack at two API replicas, to rehearse what only breaks above
one: a shared Data Protection key ring, and cache eviction under ingress cookie affinity. Docker
Compose remains the inner development loop; this is additive.

```powershell
./deploy/start-cluster.ps1     # creates the cluster if missing, otherwise redeploys
./deploy/teardown.ps1          # deletes the cluster, and the data inside it
```

The deployment is three phases in a fixed order, because Kustomize has no hook mechanism:
configuration and PostgreSQL first, then migrations as a Job run to completion, and only then the
Deployments and the Ingress. An API pod therefore never starts against an unmigrated schema.

Open <https://aiframework.localtest.me:8443>. That name resolves to `127.0.0.1` publicly, so
there is nothing to add to your `hosts` file, and the certificate is self-signed so the browser
warns once. The ingress is published on 8080/8443 rather than 80/443 because those ports are
frequently taken on Windows by IIS or BranchCache through `http.sys`.

Three things that will cost you time are documented in [`CLAUDE.md`](CLAUDE.md): TLS is not
optional in Production because the session cookie is `Secure`; configuration keys need double
underscores; and the migration Job is deleted before being re-applied, because a completed Job
has immutable fields.

> The local overlay commits a PostgreSQL password and a self-signed private key on purpose.
> They are throwaway values for a localhost-only cluster that is never deployed, each file says
> so in its own header, and neither is a pattern to copy into an overlay that targets a real
> environment.

### Ports

| Port | What | Source |
| --- | --- | --- |
| 5173 | Vite dev server | `frontend/vite.config.ts` |
| 5234 | API | `src/Api/Properties/launchSettings.json` |
| 4173 | Vite preview, used by the e2e suite | `frontend/vite.config.ts` |
| 55433 | Development PostgreSQL, data persists | `docker-compose.yml` |
| 55432 | End-to-end PostgreSQL, throwaway | `docker-compose.e2e.yml` |
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
| `GET` | `/health` | anonymous | Liveness; never touches the database |
| `GET` | `/health/ready` | anonymous | Readiness; checks PostgreSQL |

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

The interactive reference is Scalar at `/scalar/v1`, and it is **Development only** — a deployed
instance must not publish its endpoint surface, and an integration test asserts that in both
directions.

## Frontend application

```
frontend/src/
├── api/            Generated schema, typed client, endpoint wrappers
├── features/
│   ├── auth/       Login, register, change password, RequireAuth guard
│   └── orders/     List, detail, place-order form, query hooks
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
| `/account/password` | `ChangePasswordPage` | authenticated |

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
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
```

Generation runs the whole application, which is why both environment variables are required: the
startup guard rejects an empty connection string, and a durable Wolverine dials PostgreSQL. The
connection string is never actually opened. It is an explicit MSBuild target rather than part of
`dotnet build`, so that an ordinary build stays ordinary.

**Wolverine handler adapters.** Wolverine builds its adapters with Roslyn, and Release ships
without the compiler because it costs 33MB. Release loads adapters generated ahead of time under
`src/Api/Internal/Generated`. After adding or changing a handler:

```bash
dotnet run --project src/Api -- codegen write
```

This is the trap the Release dimension in CI exists for: stale generated code leaves Debug green
and the build succeeding, and fails only in Release, at startup.

**EF Core migrations.** Never hand-edit an applied migration; add a new one.
`.claude/hooks/protect-migrations.ps1` enforces this against git history, so a migration you have
just generated and not yet committed is still yours to adjust, while one that is in `HEAD` is
refused even if you delete the file first.

## Testing

```bash
dotnet test                          # 246 test cases across four projects
npm test --prefix frontend -- --run  # Vitest
npm run e2e --prefix frontend        # Playwright
```

Inside Claude Code, `/verify` runs all of the above plus both lint steps and reports what
actually ran, skipping any toolchain that is not installed rather than reporting a false pass.

| Suite | What belongs there |
| --- | --- |
| `Domain.Tests` | Invariants and domain events, no infrastructure |
| `Application.Tests` | Handlers against substituted ports; architecture tests |
| `Infrastructure.Tests` | Repositories, outbox, caching, against real PostgreSQL via Testcontainers |
| `Api.IntegrationTests` | The real pipeline through `WebApplicationFactory` |
| `frontend/src/**/*.test.tsx` | Components and hooks, with MSW standing in for the API |
| `frontend/e2e/*.spec.ts` | Two browser journeys against a built preview bundle |

Testcontainers needs a running Docker daemon. The caching behavior and the rate limiter are both
disabled by default under test and re-enabled by the specific suites that exercise them, so that
neither becomes an intermittent failure in tests that are not about them.

## Continuous integration

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push to `main` and every
pull request:

| Job | What it proves |
| --- | --- |
| `backend (Debug)` / `backend (Release)` | Builds and tests in both configurations |
| `generated code is current` | Re-runs `codegen write` and fails on any diff |
| `api contract is current` | Regenerates the OpenAPI document and the TypeScript schema, and fails on any diff |
| `frontend` | Lint, build, and Vitest |
| `e2e` | Playwright, gated behind the fast jobs |

Release is a separate matrix leg rather than an afterthought: Release was broken in this
repository for the whole life of the Wolverine spike without anyone noticing, because
`dotnet build` succeeded with zero warnings and only the startup failed.

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
  hook blocks the commit. The committed development connection string and the local Kubernetes
  overlay are deliberate, documented exceptions: throwaway credentials for localhost-only
  containers that are never deployed.

Per-language detail lives in the `dotnet-conventions`, `dotnet-testing`, `react-conventions`, and
`react-testing` skills, and each layer has a `CLAUDE.md` of its own.

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

Record a new one with `/adr <title>`.

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
2. Scaffold with `/feature <name>` or `/react-feature <name>` so the layering and the tests come
   out right the first time.
3. Keep the dependency rule intact. The hook will stop you, but the hook is a backstop, not a
   design tool.
4. Regenerate and commit any affected [generated code](#generated-code).
5. Run `/verify` before opening a pull request. CI runs the same thing plus the Release
   configuration.
6. Record anything architecturally load-bearing with `/adr <title>`.

Issues and pull requests go to <https://github.com/Surentz/AIFrameWork>.

## License

[Apache License 2.0](LICENSE). The copyright line in `LICENSE` is still the unfilled template
placeholder; set it before publishing this anywhere that matters.
