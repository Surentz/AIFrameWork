# AIFrameWork

.NET backend + React frontend, Clean Architecture, single repo.

## Versions

| | Version | Where it is pinned |
|---|---|---|
| .NET SDK | 10.0.400 | `.github/workflows/ci.yml`. There is no `global.json`, so any 10.x SDK builds |
| Target framework | net10.0 | `Directory.Build.props`, alongside `LangVersion` 14.0 |
| Node | 24.20.0 | `.github/workflows/ci.yml`. 24.15.0 is the hard floor — see below |
| React | 19.3.0 | `frontend/package-lock.json`, declared `^19.2.8` |
| Vite | 8.3.0 | `frontend/package-lock.json`, declared `^8.2.2` |
| TypeScript | 6.0.3 | `frontend/package-lock.json`, declared `~6.0.2` |

Re-verified 2026-09-11 from `dotnet --list-sdks`, `node --version`, and the *lockfile* rather
than `package.json` — the declared ranges are carets, so the resolved version drifts above the
declared one and the two are worth keeping distinct. CI is the authority for the SDK and Node,
because nothing in the repo pins either: without a `global.json` the SDK is whatever the machine
has, and Node is whatever is on `PATH`.

**Node 24.15.0 is a floor, not a preference.** Below it, `npm install` in `frontend/` fails
outright: npm's own `engines` requirement rejects the upgrade, jsdom refuses to install, and the
npm that ships with older 24.x crashes in its dependency resolver
(`Cannot read properties of null (reading 'edgesOut')`). `scripts/install-prereqs.ps1` checks
this floor.

> **This machine now matches the table.** Re-checked 2026-09-12: `dotnet --list-sdks` shows only
> `10.0.400`, and `node --version` reports `v24.20.0` — both exactly the pinned versions above.
> The gap noted here on 2026-09-11 (SDK 10.0.204/10.0.111, Node 24.19.0) is gone; a green local
> build is now real evidence about CI on the SDK/Node axis. Re-verify with the same two commands
> before trusting this note itself, since nothing in the repo pins either and the machine can
> drift again silently.

## Layout

| Path | Contents |
|---|---|
| `src/Domain` | Entities, value objects, domain events, domain exceptions |
| `src/Application` | Use cases, ports, `Result<T>`, validators |
| `src/Infrastructure` | EF Core, repositories, external clients |
| `src/Api` | Controllers, DTOs, exception handling, composition root |
| `frontend` | Vite + React workspace |
| `tests` | Test projects, one per layer |

## The dependency rule

Dependencies point inward. This is enforced by `.claude/hooks/dependency-rule.ps1`,
which blocks the edit rather than warning about it.

| From ↓ / To → | Domain | Application | Infrastructure | Api |
|---|---|---|---|---|
| **Domain** | — | ✗ | ✗ | ✗ |
| **Application** | ✓ | — | ✗ | ✗ |
| **Infrastructure** | ✓ | ✓ | — | ✗ |
| **Api** | ✓ | ✓ | ✓ DI only | — |

> The `Api → Infrastructure` cell is the one row the hook does **not** enforce: nothing
> distinguishes a `services.AddScoped<>()` registration from a controller reaching into a
> repository, so "DI only" is carried by review and `dotnet-reviewer`. Every other cell blocks.

`Domain` additionally may not reference `Microsoft.EntityFrameworkCore`,
`Microsoft.AspNetCore`, `Microsoft.Extensions.DependencyInjection`, `System.Data`,
or `System.ComponentModel.DataAnnotations`.

## Non-negotiables

- **Warnings are errors.** All three sources — compiler, analyzers, build — see
  `Directory.Build.props`. Fix diagnostics; do not suppress them without a justification comment.
  Repo-wide exemptions live in `.editorconfig` as `severity = none` rules — that file is the
  place to look for what's off and why. Everything narrower is a local `#pragma warning
  disable`/`restore` pair at the point of use, and every one of those carries a justification
  comment right above it explaining why the rule's intent doesn't apply there (see, for example,
  `src/Application/Abstractions/Messaging.cs` and `tests/Infrastructure.Tests/Persistence/
  PostgresFixture.cs`). Grep for `#pragma warning disable` if you need the current, exact list —
  it grows as new sites are justified, so no count is kept here.
- **Nullable is enabled.** A missing null check does not compile.
- **`required` keyword in `Domain`, never `[Required]`.** DataAnnotations belong on `Api` DTOs.
- **Never `catch (Exception)`.** The global `IExceptionHandler` receives it as a parameter.
  CA1031 is an error everywhere except one file-scoped exemption in `.editorconfig`, for the
  outbox's `BackgroundService` pumps, which have no such parameter and must not die mid-loop.
  `throw;`, never `throw ex;`.
- **Never hand-edit an applied EF migration.** Add a new one. `.claude/hooks/protect-migrations.ps1`
  enforces this, keying on git history rather than on the file existing: a migration you have
  just generated and not yet committed is still yours to adjust, and one that is in `HEAD` is
  refused even if you delete the file first.
- **No secrets in `appsettings*.json`.** Use `dotnet user-secrets` or environment variables.

## Commands

| Command | Does |
|---|---|
| `/feature <name>` | Scaffold a feature across all four layers, with tests |
| `/react-feature <name>` | Scaffold a React feature |
| `/verify` | Build, test, and lint both stacks |
| `/adr <title>` | Record an architecture decision |

## Running locally

```powershell
./scripts/dev.ps1                                            # all of the below, in three windows
```

Or by hand:

```bash
docker compose up -d --wait                                  # dev Postgres on 55433
dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
dotnet run --project src/Api                                 # then `npm start` in frontend/
```

Both open a browser tab of their own: the API reference at `/scalar/v1` (from
`launchSettings.json`) and the app at `http://localhost:5173` (from `vite.config.ts`). For
one-keystroke startup in Rider or Visual Studio, and the gotchas that come with it, see
[docs/local-development.md](docs/local-development.md).

Two databases, two ports, and they are meant to coexist: **55433** is the dev database from
`docker-compose.yml` (named volume, data persists); **55432** is the e2e one from
`docker-compose.e2e.yml` (throwaway). Override either with `DEV_PG_PORT` / `PG_PORT`.

The dev connection string is committed in `src/Api/appsettings.Development.json` — throwaway
credentials against a localhost-only container that is never deployed, the same judgement
already applied to `docker-compose.e2e.yml`. The no-secrets-in-`appsettings*.json` rule still
holds for everything else.

The two *databases* coexist happily, but the two *API* processes do not: `npm run e2e` starts
its own API on 5234 — the same port `dotnet run` uses — with `reuseExistingServer: false`. Stop
the dev API before an e2e run, or set `API_PORT`.

**The gotcha that will cost you an afternoon: `dotnet ef` cannot see user-secrets.** Migrations
run through `src/Infrastructure/Persistence/DesignTimeDbContextFactory.cs`, which reads only the
`ConnectionStrings__Default` environment variable — so `database update` against anything but
the default needs it passed explicitly:

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure
```

The reverse also bites: a user-secret sits *above* `appsettings.Development.json` in the
configuration order, so a stale `ConnectionStrings:Default` there silently wins over the
committed value at runtime while `dotnet ef` ignores it. Check with
`dotnet user-secrets list --project src/Api` if the app and the migrations disagree about which
database they are talking to.

## Running on Kubernetes

A local kind cluster that runs the whole stack at **two API replicas**, to rehearse the
things that only break above one. `docker compose` remains the inner development loop;
this is additive.

```powershell
./deploy/deploy.ps1 -CreateCluster   # first run: creates the cluster and ingress-nginx
./deploy/deploy.ps1                  # later runs: rebuild, migrate, roll out
```

Then open `https://aiframework.localtest.me` — that name resolves to `127.0.0.1` publicly,
so there is nothing to add to `hosts`. The certificate is self-signed.

> On a machine where host ports 80/443 are already taken (IIS, BranchCache, or anything else
> bound via `http.sys`), `deploy/kind-cluster.yaml` maps the ingress to 8080/8443 instead —
> in that case browse `https://aiframework.localtest.me:8443`.

Three things that will cost you time:

- **TLS is not optional.** `ASPNETCORE_ENVIRONMENT=Production` sets
  `CookieSecurePolicy.Always`, so over plain HTTP the browser discards the session cookie
  silently: login appears to succeed and every later request is a 401, with nothing in the
  logs.
- **Config keys need double underscores.** `Cache__Enabled` and `ForwardedHeaders__Enabled`, not
  `Cache_Enabled`. A single underscore binds nothing, warns nothing, and leaves the default in
  place.
- **Migrations run as a Job, before the rollout**, via a self-contained `dotnet ef migrations
  bundle` — which is what keeps the EF Design package out of the runtime image
  (`src/Infrastructure/CLAUDE.md`'s 7.9MB → 37MB note). The script deletes the Job before
  re-applying it, because a completed Job has immutable fields.

The overlay's `secret.yaml` and `tls.yaml` commit real credentials — a Postgres password and a
self-signed private key — on purpose: throwaway values for a localhost-only cluster that is
never deployed, the same judgement already applied to `docker-compose.e2e.yml` and the dev
connection string. Each file's own header says so, and `tls.yaml`'s carries the `openssl`
command to regenerate the certificate when it expires (2027-09-10). Neither is a pattern to copy
into an overlay that targets a real environment.

Cache eviction correctness depends on the ingress's cookie affinity: `HybridCache` is L1-only,
so a write handled by one pod cannot evict an entry held by the other. See ADR 0010.

`./deploy/e2e-k8s.ps1` runs the Playwright suite against this cluster — a gate that exercises
durable Wolverine, caching on, two replicas, and the real rate limit, none of which the compose
stack does. It gates readiness on `/api/auth/me`, not `/health`: the ingress routes `/health` to
the web pod, whose `nginx.conf` serves the SPA for any unmatched path, so it answers 200 whether
or not a single API pod is up. See ADR 0012.

### One-click start/stop

`local-run/control-panel.bat` is a double-clickable menu for both setups, for anyone who would
rather not open a terminal — it has no logic of its own beyond the menu:

| Menu option | Runs |
|---|---|
| Install/check prerequisites | `scripts/install-prereqs.ps1` — see below |
| Start dev loop | `scripts/dev.ps1` — the plain local dev loop |
| Stop dev loop | `scripts/stop-dev.ps1` — kills the API/Vite ports, `docker compose down` |
| Start Kubernetes | `deploy/start-cluster.ps1` — creates the kind cluster if missing, else redeploys onto it |
| Stop Kubernetes | `deploy/teardown.ps1` — `kind delete cluster`; Postgres data inside it goes with it |
| Run e2e tests (local stack) | `scripts/e2e.ps1` — stop the dev loop first, it uses port 5234 |
| Run e2e tests (against Kubernetes) | `deploy/e2e-k8s.ps1` — deploy it first with "Start Kubernetes" |
| Open last e2e report | `scripts/e2e-report.ps1` |

The `.ps1` scripts it calls are the source of truth and work the same run directly.

`scripts/install-prereqs.ps1` is what a genuinely new machine needs run first — it checks for
(and installs via `winget` whatever is missing) the .NET SDK, Node.js, Docker Desktop, `kubectl`,
`kind`, `k9s`, and Playwright's chromium browser. It only installs what is entirely absent; a
tool that is present but older
than expected is reported, not silently upgraded, since upgrading Docker Desktop or Node.js
touches every other project on the machine, not just this one. It also flags a global `~/.npmrc`
pinning `os=`/`cpu=` to the wrong platform — the exact cause of a `npm install` failure
(`Cannot find native binding`, rolldown's Windows binding silently never downloaded) hit and
fixed on this repo once already. Docker Desktop's own first-run setup (WSL2 backend, license
terms, a restart) cannot be scripted unattended; the script starts that install and says so.

## Wolverine codegen

Wolverine builds its handler adapters with Roslyn, and **Release ships without the compiler** —
it costs 33MB (measured: a Release publish is 17MB without it, 50MB with). Release instead loads
adapters generated ahead of time and committed under `src/Api/Internal/Generated`.

**After adding or changing a Wolverine handler, regenerate them:**

```bash
dotnet run --project src/Api -- codegen write
```

Then commit the result. Debug does not need this — it still compiles adapters at startup — which
is exactly the trap: stale generated code leaves Debug green and the build succeeding, and breaks
only in Release, at startup. `WolverineCodegenTests` exists to catch that in the Debug suite; if
it fails, the fix is the command above. CI additionally re-runs `codegen write` and fails on
any diff, catching generated code that still loads but has drifted.

`Program.cs` therefore routes to `RunJasperFxCommands(args)` when args are present, and to plain
`RunAsync()` when they are not — which is what makes `codegen write` reachable without paying for
it on every ordinary start. JasperFx discovers commands by reflecting over every loaded assembly
and prints a line per assembly as it goes; with no args there is no command to find, so the
no-args branch skips that entirely and keeps `dotnet run` and F5 quiet.

`WebApplicationFactory` passes args of its own, so tests still take the JasperFx branch and need
`JasperFxEnvironment.AutoStartHost` — set once for the whole test assembly in
`tests/Api.IntegrationTests/JasperFxTestEnvironment.cs`. Without it, 23 of the 24 integration
tests fail with "The server has not been started".

See ADR 0005.

## The API contract

`openapi/AiFramework.Api.json` is generated from the application and **committed**.
`frontend/src/api/schema.d.ts` is generated from it. Both are checked by CI.

**After changing a controller, a DTO, or a `[ProducesResponseType]`, regenerate both:**

```bash
dotnet restore src/Api
ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
  Wolverine__Durable=false \
  dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
npm run generate:api --prefix frontend
```

Then commit the result.


`dotnet restore` is a separate first step because `dotnet msbuild` — unlike `dotnet build` —
does not restore implicitly, so a fresh clone fails with NETSDK1004. It cannot be folded in as
`-t:"Restore;Build;..."` either: MSBuild evaluates the project once, before Restore writes
NuGet's props, and the OpenAPI XML-comment source generator then fails with CS9137 about
interceptors.
**An explicit target, not part of `dotnet build`.** Generation runs the whole application, so it
needs both environment variables: without a connection string it fails on the startup guard in
`Program.cs`, and with one but still durable, Wolverine's startup migration dials Postgres
(ADR 0005). The connection string is never actually opened — it only has to be non-empty.

Generating on every build was tried and reverted: it made a plain `dotnet build` of `src/Api`
fail without those variables *even with the database running*, which would have broken every
developer's build and the existing CI `backend` and `e2e` jobs. Keeping it on a target means an
ordinary build stays ordinary, and only the two commands above and CI's `contract` job pay
the cost.


## Session invalidation

Every authenticated request costs one uncached read of the user's security stamp, in
`Program.cs`'s `OnValidatePrincipal`. That read is deliberately **not** cached: `HybridCache`
here is L1-only and cannot be evicted across pods, so caching it would let a revoked session
survive on another replica for the length of the TTL.

Rotating `User.SecurityStamp` invalidates every cookie already issued for that user. Three
things rotate it: a password change, the failed sign-in that locks an account, and
`POST /api/auth/sign-out-everywhere`.

Two things that will cost you time:

- **A cookie carrying no stamp claim is rejected.** That is what retires sessions issued before
  ADR 0011, and it means any change to `SessionClaims.SecurityStamp`'s value signs everybody out.
- **Changing a password re-issues the caller's own cookie.** `ChangePassword` returns a
  `SessionView` rather than a bool for exactly this reason; without the re-issue in
  `AuthController`, changing your own password signs you out.

Nothing on the auth path is cached, which is what stops a stale stamp being served from the
query cache. Do not make `GetUser` `ICacheable`.

See ADR 0011.

## Caching

Queries opt in by implementing `ICacheable` (`src/Application/Abstractions/Caching.cs`); commands
opt in to eviction with `IInvalidatesCache`. `GetOrders` and `GetOrder` are cached at thirty
seconds; `PlaceOrder` evicts both for the caller who placed the order. `GetProducts` and
`GetProduct` are cached the same way, and `CreateProduct`/`UpdateProduct` evict them. Nothing on
the auth path is cached, deliberately and permanently — ADR 0008's lockout state must be read
every time.

Four things that will cost you time:

- **The cache stores `TResponse`, not `Result<T>`.** `Result<T>.Error` throws when read on a
  success, so `System.Text.Json` fails on a successful `Result` and the `internal` constructor
  makes deserializing one impossible. The behavior rebuilds `Result.Success(value)` on a hit.
- **`CacheKey` must NOT contain a user id.** The behavior prepends the query type and
  `ICurrentUser.Id` via `CacheScope`, which is also what `IInvalidatesCache.Tags` is composed
  with — the tag is the key's own prefix, and that is the whole eviction mechanism. An
  `ICacheable` query dispatched with no current user throws rather than sharing one entry across
  every caller.
- **`ICurrentUser.Id` must stay stable once resolved.** `HybridCache` runs its cache-miss factory
  without the ambient `HttpContext`, by design, so a *live* implementation reading the claim on
  every access reports the caller unauthenticated inside the factory and every cache miss answers
  401. `CurrentUser` memoizes the first non-null id for exactly this reason; a second
  implementation of the port that re-reads its source reintroduces the bug silently.
- **The cache is OFF under test.** `ApiFactory` sets `Cache:Enabled=false` and
  `frontend/playwright.config.ts`'s `webServer` env sets `Cache__Enabled=false` — not
  `docker-compose.e2e.yml`, which runs only Postgres. `Orders/OrderCachingTests` turns it back on
  for itself — `WithWebHostBuilder` over the shared `ApiFactory`, so it keeps the one Postgres
  container — the same split `AuthRateLimitTests` uses for the rate limiter.

**The catalogue is the one cached read that is not per-user data.** Products are global, but the
cache is scoped per caller by construction, so `CreateProduct`'s eviction reaches only the caller
who made the write — everyone else keeps their cached pages until the thirty seconds lapse. The
TTL, not the eviction, is what bounds how long an edit stays invisible to other people. That is
accepted rather than worked around: an unscoped cache path would give up the one property that
makes this cache safe to use without thinking. See ADR 0013.

No test waits for a TTL to lapse; `HybridCache` expires on its own clock, which `IClock` cannot
reach. The only TTL arithmetic is `CacheDuration.Clamp`, tested directly.

See ADR 0009.


## CI

`.github/workflows/ci.yml` runs what `/verify` runs, on every push to `main` and every pull
request: backend build and test, frontend lint/build/test, and the Playwright e2e suite.

Two things it does that a local `/verify` does not:

- **Builds and tests both Debug and Release.** Release was broken in this repo for the whole life
  of the Wolverine spike without anyone noticing, because `dotnet build` succeeded with zero
  warnings and only the *startup* failed. Debug alone is not evidence.
- **Checks the committed generated code is current**, by re-running `codegen write` and failing on
  any diff — see "Wolverine codegen" above.

## More context

Each layer has its own `CLAUDE.md`, loaded when you work in that directory.
Conventions live in the `dotnet-conventions`, `dotnet-testing`, `react-conventions`,
and `react-testing` skills.

Design rationale: `docs/superpowers/specs/2026-08-27-claude-framework-design.md`
