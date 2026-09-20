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
| `src/Worker` | The job host: composition, health endpoints, its own generated adapters |
| `frontend` | Vite + React workspace |
| `tests` | Test projects, one per layer |

## The dependency rule

Dependencies point inward. This is enforced by `.claude/hooks/dependency-rule.ps1`,
which blocks the edit rather than warning about it.

| From ↓ / To → | Domain | Application | Infrastructure | Api | Worker |
|---|---|---|---|---|---|
| **Domain** | — | ✗ | ✗ | ✗ | ✗ |
| **Application** | ✓ | — | ✗ | ✗ | ✗ |
| **Infrastructure** | ✓ | ✓ | — | ✗ | ✗ |
| **Api** | ✓ | ✓ | ✓ DI only | — | ✗ |
| **Worker** | ✓ | ✓ | ✓ DI only | ✗ | — |

> The `Api → Infrastructure` and `Worker → Infrastructure` cells are the two rows the hook does
> **not** enforce: nothing distinguishes a `services.AddScoped<>()` registration from a controller
> reaching into a repository, so "DI only" is carried by review and `dotnet-reviewer`. Every other
> cell blocks.

> **`Api` and `Worker` are siblings, not layers** — two composition roots over the same three
> inner layers (ADR 0016). Neither may reference the other. `Worker → Api` is blocked for a
> concrete reason beyond tidiness: it is exactly what would let the two share one Wolverine
> generated-code tree, and keeping them separate is what makes each host's `codegen write`
> independently correct.

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
| `/job <name>` | Add a background job: message, handler, registration, tests, regenerated adapters |

## Running locally

```powershell
./scripts/dev.ps1                                            # all of the below, in three windows
./scripts/dev.ps1 -WithSeq                                   # same, plus Seq at localhost:55341
./scripts/worker.ps1                                         # just the job worker, in this window
```

The three windows are the API (5234), the **job worker** (5235) and Vite (5173). `worker.ps1` is
for restarting only the worker — which `dotnet run --project src/Worker -- codegen write` requires
before new adapters take effect, and which is otherwise a stop-everything-and-start-again.

`-WithSeq` starts `docker-compose.yml`'s `observability` profile alongside Postgres and points
the launched API at it (`Observability__Otlp__Enabled`/`__Endpoint`, set on the API's own
process environment, never baked into `appsettings.Development.json` — every developer's `dotnet
run` would otherwise try to export to a collector nobody started). `scripts/stop-dev.ps1` always
passes `--profile observability` to `docker compose down`, whether or not `-WithSeq` was used —
confirmed empirically, not assumed, that a bare `docker compose down` does NOT stop a
profile-started container even when it is currently running. See the "Logging" section below.

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

A local kind cluster that runs the whole stack at **two API replicas across two worker nodes**,
to rehearse the things that only break above one. `docker compose` remains the inner development
loop; this is additive.

**Two replicas means two failure domains, not two processes.** `deploy/kind-cluster.yaml`
declares a control-plane node plus two workers; kind taints the control-plane once workers
exist, so application pods run on the workers while ingress-nginx stays put via its
`ingress-ready` nodeSelector. `api` spreads with `whenUnsatisfiable: DoNotSchedule` — the soft
variant would degrade to co-located pods under the smallest scheduling pressure, which is the
state this exists to prevent and the one nobody would notice. Price: two more kubelets and
container runtimes on your machine, roughly a gigabyte before an application pod starts.

**Autoscaling is real but bounded, and the bound is the point.** `k8s/base/autoscaling.yaml`
carries HPAs for `api` (CPU and memory) and `web` (CPU only — nginx serving a static bundle has
flat memory), with `minReplicas: 2` as a floor rather than a starting point, and
PodDisruptionBudgets for all three workloads. The ingress pins each user to a pod for an hour, so
**scaling out does not rebalance anyone already connected** — a new pod only receives sessions
that start after it does. Autoscaling buys headroom for new traffic and survives node pressure;
it does not even out load across existing sessions. See ADR 0018, and ADR 0010's 2026-09-20
amendment for why the affinity cannot currently be removed.

Three things that will bite:

- **metrics-server is installed by `deploy.ps1 -CreateCluster`**, patched with
  `--kubelet-insecure-tls` because kind's kubelets serve metrics with a certificate it does not
  trust. Without it every HPA reports `unknown` and never scales — as a *condition on the HPA*,
  not as a deploy failure, so the cluster looks healthy while the autoscalers do nothing.
- **The worker has a disruption budget but no autoscaler**, and its budget is
  `maxUnavailable: 1` rather than `minAvailable: 1`. On a one-replica Deployment `minAvailable: 1`
  blocks a node drain *indefinitely* — the budget can never be satisfied while the only pod is
  evicted. Its work arrives through queues, so queue depth is the signal an autoscaler would
  need; CPU would scale it down exactly when it is blocked and falling behind.
- **Postgres is pinned to whichever node it first scheduled on**, by kind's node-local storage
  class. Draining that node will not reschedule it. Target a different node for a drain test.

```powershell
./deploy/deploy.ps1 -CreateCluster   # first run: creates the cluster and ingress-nginx
./deploy/deploy.ps1                  # later runs: rebuild, migrate, roll out
./deploy/deploy.ps1 -WithObservability   # same, plus the OTel Collector -> OpenSearch stack
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
  (`src/Infrastructure/CLAUDE.md`'s 7.9MB → 37MB note). The script deletes every Job before
  re-applying it, because a completed Job has immutable fields — `-WithObservability` adds a
  second one (`opensearch-ism-policy`) alongside `migrate`, handled the same way.

The overlay's `secret.yaml` and `tls.yaml` commit real credentials — a Postgres password and a
self-signed private key — on purpose: throwaway values for a localhost-only cluster that is
never deployed, the same judgement already applied to `docker-compose.e2e.yml` and the dev
connection string. Each file's own header says so, and `tls.yaml`'s carries the `openssl`
command to regenerate the certificate when it expires (2027-09-10). Neither is a pattern to copy
into an overlay that targets a real environment.

Cache eviction correctness depends on the ingress's cookie affinity: `HybridCache` is L1-only,
so a write handled by one pod cannot evict an entry held by the other. That affinity lives on
its own `aiframework-api` Ingress and must stay there — annotations apply to a whole Ingress,
so putting the SPA's `/` path back alongside `/api` issues a *second* `aiframework.route`
cookie, which silently disables affinity rather than erroring. See ADR 0010.

`./deploy/e2e-k8s.ps1` runs the Playwright suite against this cluster — a gate that exercises
durable Wolverine, caching on, two replicas, and the real rate limit, none of which the compose
stack does. It gates readiness on `/api/auth/me`, not `/health`: the ingress routes `/health` to
the web pod, whose `nginx.conf` serves the SPA for any unmatched path, so it answers 200 whether
or not a single API pod is up. See ADR 0012. `-WithObservability` is deliberately **not** part of
this gate — a log store has no business in the readiness path of an e2e run.

### `-WithObservability`

Opt-in, via a Kustomize Component (`k8s/components/observability`) that
`k8s/overlays/local-observability/kustomization.yaml` alone references — `k8s/overlays/local`
never does, so a plain `deploy.ps1` run is unaffected. It is also the largest thing in the
cluster by a wide margin: OpenSearch wants real JVM heap, and Dashboards is a second Node process
on top, so only reach for this when the logging pipeline itself is what you're rehearsing.

- **The OTel Collector is not optional.** OpenSearch does not ingest OTLP natively — an
  application pointed straight at it would deliver nothing, silently. The collector's
  `opensearch` exporter is what bridges the two, confirmed empirically (a real .NET OTLP export,
  through this repo's own `ObservabilityRegistration.BuildOtlpEndpoint`, landing in a live
  OpenSearch index) while building this, not assumed from documentation.
- **Index rollover comes from the exporter, not from OpenSearch.** `logs_index_time_format`/
  `traces_index_time_format: yyyy.MM.dd` on the collector's `opensearch` exporter is what
  produces one physical index per UTC day (`otel-logs-2026.09.13`, and so on) — without it
  everything lands in one never-rolling index and the ISM retention policy below has nothing
  dated to delete.
- **The ISM policy attaches itself.** Its `ism_template` field — not a separate index template,
  not a per-index `_ism/add` call — makes OpenSearch apply the policy to any new index matching
  `otel-logs-*`/`otel-traces-*` the moment it is created, confirmed by creating a fresh matching
  index and reading the policy back off `_plugins/_ism/explain`. `opensearch-ism-policy` is a
  one-shot `Job`, not a `CronJob`: the policy is a standing cluster rule once set, so nothing is
  gained by reapplying it on a schedule — a second `PUT` on an existing policy answers `409`, and
  the Job's own script treats that as success rather than failure.
- **`Observability__Otlp__Enabled`/`__Endpoint`** are added to the same `app-config` ConfigMap
  `k8s/overlays/local/config.yaml` already defines, by a Kustomize patch inside the component —
  double underscores, like every other key there.

### One-click start/stop

`local-run/control-panel.bat` is a double-clickable menu for both setups, for anyone who would
rather not open a terminal — it has no logic of its own beyond the menu:

| Menu option | Runs |
|---|---|
| Install/check prerequisites | `scripts/install-prereqs.ps1` — see below |
| Start dev loop | `scripts/dev.ps1` — the plain local dev loop: Postgres, API, **job worker**, Vite |
| Start dev loop + Seq | `scripts/dev.ps1 -WithSeq` — same, plus Seq at `localhost:55341` |
| Start job worker only | `scripts/worker.ps1` — restarts just the worker, leaving a working API and Vite alone. Runs in the foreground, so you watch its log; `codegen write` needs a worker restart to take effect |
| Stop dev loop | `scripts/stop-dev.ps1` — kills the API/worker/Vite ports, tears down the database (and Seq, if it was started) |
| Start Kubernetes | `deploy/start-cluster.ps1` — creates the kind cluster if missing, else redeploys onto it |
| Stop Kubernetes | `deploy/teardown.ps1` — `kind delete cluster`; Postgres data inside it goes with it |
| Run e2e tests (local stack) | `scripts/e2e.ps1` — stop the dev loop first, it uses port 5234 |
| Run e2e tests (against Kubernetes) | `deploy/e2e-k8s.ps1` — deploy it first with "Start Kubernetes" |
| Open last e2e report | `scripts/e2e-report.ps1` |
| Pull latest | `scripts/update-branch.ps1` — fast-forwards whatever branch is currently checked out |

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

**There are TWO generated trees** — `src/Api/Internal/Generated` and
`src/Worker/Internal/Generated`. `TypeLoadMode.Static` resolves pre-built types out of each host's
own `opts.ApplicationAssembly`, and the worker cannot share the Api's: that needs a `Worker → Api`
reference the dependency rule forbids. ADR 0016.

**After adding or changing a Wolverine handler, regenerate the tree(s) it belongs to:**

```bash
dotnet run --project src/Api -- codegen write      # event-path handlers
dotnet run --project src/Worker -- codegen write   # job handlers, and JobUserMiddleware
```

Then commit the result. Debug does not need this — it still compiles adapters at startup — which
is exactly the trap: stale generated code leaves Debug green and the build succeeding, and breaks
only in Release, at startup. `WolverineCodegenTests` exists to catch that in the Debug suite; if
it fails, the fix is the command above; `WorkerCodegenTests` is its counterpart for the worker's
tree. CI additionally re-runs **both** `codegen write` commands and fails on any diff, catching
generated code that still loads but has drifted.

**CI's Linux output is the authority, and Windows does not always match it.** On the Quartz
branch, `codegen write` on Windows put `RebuildOrderReportHandler`'s `jobCurrentUser` resolution
after `queryDispatcher`, and CI on Linux put it before — stable on each platform, not affected by
culture, and not diagnosed further. The two orders do the same thing. If a Windows regeneration
reorders statements in a handler you did not change, keep the committed version of that file;
CI's "generated code is current" job is what decides.

Two codegen failures compile perfectly well and surface only when you run the command, both found
that way while building the job framework: JasperFx will not upcast a concrete message to an
interface for a middleware parameter, and it refuses service location under Wolverine 6's
`NotAllowed` default — which a job handler injecting a dispatcher triggers, since ADR 0003's
reflection-free dispatchers take `IServiceProvider`. See `src/Worker/CLAUDE.md`.

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

`PlaceOrder` snapshots the product's name and price onto the order, so a later `UpdateProduct`
cannot change what an existing order says it cost — and cannot stale a cached order page either.
That is what keeps product writes out of the order cache's eviction path entirely.

No test waits for a TTL to lapse; `HybridCache` expires on its own clock, which `IClock` cannot
reach. The only TTL arithmetic is `CacheDuration.Clamp`, tested directly.

See ADR 0009.

## Jobs

**Jobs run in the worker. The API listens to nothing.** That is the whole rule, and it is
enforced rather than intended: `ApiPublishesOnlyTests` asserts against the runtime's own endpoint
list that no `jobs_*` queue has a listener on the API host, and `JobDeliveryTests` asserts the
mirror image on the worker. The API registers *routing* for every lane — publishing is how a job
starts — and never `ListenToPostgresqlQueue`.

The reason is the API's own health: a job sharing the API process competes for the thread pool,
the GC heap under a 768Mi limit, and the Npgsql pool, and every API rollout would kill work
in flight. See ADR 0016.

A job is a message with a lane, nothing more:

```csharp
public sealed record RebuildOrderReport(Guid OwnerId) : IUserScopedJob
{
    public static JobLane Lane => JobLane.Heavy;
}
```

| Lane | Queue | For | Parallelism per pod |
|---|---|---|---|
| `Light` | `jobs_light` | Milliseconds to seconds, a round trip or two, negligible CPU | 8 |
| `Heavy` | `jobs_heavy` | Seconds to minutes, CPU- or memory-bound, or fanning over a large result set | 2 |

The lane picks the queue; `Jobs__Queues` picks which host listens. Both lanes run on one worker
today — splitting them onto differently-sized Deployments later is a manifest copy and no code
change. Two queues earn their place immediately anyway: without them one thirty-second report
blocks a queue of one-second emails behind it.

Enqueue through `IJobScheduler` (`src/Application/Abstractions/Jobs.cs`); Wolverine never appears
in `Application`. Every job type must be registered in
`src/Infrastructure/Jobs/JobRegistration.cs` — explicit and greppable, mirroring `AddMessaging()`,
with `JobRegistrationTests` failing the build on an omission.

**Six things that will cost you time:**

- **An enqueue is NOT transactional with the caller's work.** `EnqueueAsync` from a command
  handler publishes immediately, so a command whose transaction then fails still runs the job.
  **For a job that must not be lost, raise a domain event and enqueue from its handler** —
  `DomainEventsInterceptor` writes the outbox row in the same `SaveChangesAsync` as the aggregate,
  so the job exists if and only if the command committed. `OrderPlacedConfirmationHandler` is the
  reference. This is measured, not preference: Wolverine's own EF Core outbox was the intended
  mechanism and publishing through it *enrolls the DbContext*, opening a transaction that
  `UnitOfWork`'s plain `SaveChangesAsync` then cannot commit. `JobEnqueueMechanismTests` pins it.
- **There are TWO generated-code trees now.** `dotnet run --project src/Api -- codegen write`
  *and* `dotnet run --project src/Worker -- codegen write`. Debug stays green with either one
  stale; only Release breaks, at startup. CI checks both.
- **A job cannot evict the API's cache.** `HybridCache` is L1-only, and the worker is not behind
  the ingress cookie affinity that makes eviction work between API pods at all (ADR 0010). The
  worker therefore runs with `Cache__Enabled=false` so this is explicit rather than subtle — the
  TTL, not the eviction, is what bounds how long a job's write stays invisible.
- **There is no `HttpContext` in the worker.** A job that touches user-owned data implements
  `IUserScopedJob` and carries the owner; `JobUserMiddleware` populates `ICurrentUser` from it
  before the handler runs. Forgetting is not a leak — ADR 0007 puts ownership in the query, so the
  job reads nothing — but it is a job that silently never works.
- **`ICurrentUser` is registered per host, never in `AddInfrastructure`.** The API binds it to the
  cookie's claims, the worker to the job. Binding it inside `AddJobs` replaces the API's, because
  the last registration wins, and every authenticated request then reports no caller.
- **Quartz 4 is not Quartz 3**, and most samples online are 3.x. Checked against the 4.1.0
  package: `IJob.Execute(IJobExecutionContext, CancellationToken = default)` returns `ValueTask`;
  there is no `GetJobKeys` (use `QueryJobs(new JobQuery { Group = ... })`, which pages via
  `.Items`/`.HasMore`), no `CheckExists` (it is `Exists`), no `CronExpression.IsValidExpression`
  (use `TryParse`), and no settable `SchedulerName`; misfires are set with
  `WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed)`. The built-in instance-id
  generators are `internal`, and with clustering on and nothing configured every node gets the
  id `NON_CLUSTERED` — see ADR 0017.

Retry and dead-lettering are Wolverine policy (`OnAnyException().ScheduleRetry(...).Then
.MoveToErrorQueue()`), never hand-written backoff — `ScheduleRetry` rather than
`RetryWithCooldown` on the heavy lane, because a cooldown holds a listener slot for its whole
delay and would consume half a two-slot lane.

**Scheduled jobs use Quartz as the clock (ADR 0017).** Declare one with
`JobDescriptor.Scheduled<TJob>("0 5 * * * ?")` — a Quartz cron, seconds first — and it fires on
exactly one worker, which enqueues it; Wolverine runs it like any other job. Only parameterless
jobs can be scheduled, and the compiler enforces it. Override per environment with
`Jobs__Schedules__<JobName>`; an unknown job or invalid cron fails worker startup. Quartz's tables
live in the `quartz` schema, created by an EF migration — **a Quartz upgrade that changes its
schema is a new migration**, and the worker's `SchemaProvisioning.Validate` refuses to start until
it exists. Never start a scheduler in the API.

**The transport stays PostgreSQL.** RabbitMQ is deferred with named triggers — worker replicas
sustained above four, queue polling visible in database load, a job needing priority the transport
cannot express, or a consumer outside this solution. It would not remove Postgres from the path.

Use `/job <Name>` to add one. See `src/Worker/CLAUDE.md` and ADR 0016.

## Logging

Every command and query is logged automatically. `Behaviors.LoggedAsync`
(`src/Infrastructure/Messaging/Behaviors.cs`) wraps every dispatch — `AddCommand` runs
`Logged(Validate -> handler -> Commit -> Evict)`, `AddQuery` runs `Logged(Cached(handler))` — so
no handler writes a logging call to get its outcome and duration recorded, and no handler should:
never log "handling X" or "returning failure" by hand, the behavior already reports that.

- **Success is `Debug`.** Every query goes through this pipeline; at a higher default level a
  single page load would already be "it worked" noise. A failed `Result` is levelled by its
  `ErrorKind` — `Validation`/`NotFound` stay at `Debug` (the caller being wrong, not the system),
  `Conflict`/`Unauthorized` are `Information`, anything else is `Warning`. A thrown exception logs
  `Faulted` at `Warning` and rethrows unchanged — `GlobalExceptionHandler` still owns turning it
  into a 500 and logging the exception object itself at `Error`; `LoggedAsync` never logs the
  exception, or it would double-report the same failure.
- **The behavior logs `typeof(TRequest).Name`, never the request instance.** `SignIn`,
  `RegisterUser` and `ChangePassword` carry a plaintext password field — logging the request
  object would write every password in the system to the log store, permanently.
  `SensitiveCommandLoggingTests` (`tests/Infrastructure.Tests/Messaging`) exists to catch that
  class of regression, the same way a cache key missing its user scope is caught by a test rather
  than by review alone.
- The outbox's `OutboxWorkItemProcessor` logs its three outcomes the same way: dispatched at
  `Debug`, a scheduled retry at `Information`, dead-lettering at `Warning` — see
  `src/Infrastructure/CLAUDE.md`'s Outbox section.
- **Trace continuity crosses the outbox boundary too.** A domain event delivered later, by a
  background pump, restores the W3C traceparent the raising request captured — so a delivery's
  own logs, and anything a handler logs, carry the same `TraceId` as the `POST` that caused them,
  not a fresh unrelated one. `OutboxMessage.TraceParent` and `OutboxWorkItemProcessor`'s delivery
  `Activity`; see `src/Infrastructure/CLAUDE.md`'s Outbox section for the mechanism.
- `Application` may inject `ILogger<T>` for something genuinely domain-meaningful a handler alone
  knows, never for control flow the behavior already reports. See
  `src/Application/CLAUDE.md`'s own Logging section for the fuller reasoning, including why that
  is convention-and-review-enforced rather than backed by an architecture test.

**Config keys use double underscores, same as `Cache__Enabled` elsewhere in this file:**
`Observability__Otlp__Enabled` and `Observability__Otlp__Endpoint`, never a single underscore,
which binds nothing and warns nothing. `Observability:Otlp:Enabled` defaults to `false`
everywhere — appsettings.json, every test host, CI — so nothing tries to export to a collector
that was never started; the tracer provider itself is still registered unconditionally, which is
what makes `Activity.Current` non-null and the `traceId` already written into every
`ProblemDetails` resolve to a real, correlatable value. `Observability:Otlp:Endpoint` is the OTLP
receiver's **root**, with no `/v1/logs`/`/v1/traces` suffix —
`ObservabilityRegistration.BuildOtlpEndpoint` appends the right one per signal, and does not rely
on the SDK to (confirmed empirically that it will not: see that method's own remarks for what
that cost to discover).

See `docs/superpowers/plans/2026-09-13-centralized-logging.md` for the full design and the
phased rollout, and ADR 0015 for the decision itself — MEL + a pipeline behavior over Serilog or
a base class, OTLP export over a store-specific sink.

## Notifications

An in-app feed, one row per recipient, written by domain event handlers off the outbox and read
over REST (`/api/notifications`, `/api/notifications/unread-count`). Four kinds:
`OrderPlaced`, `OrderShipped`, `OrderCancelled`, `ProductPriceChanged`. The first three notify
the buyer; the last notifies everyone who has previously ordered that product.

Five things that will cost you time:

- **A domain event handler must save its own work.** It runs on the outbox pump, *not* through
  the command pipeline, so the unit-of-work behavior that commits exactly once after a command
  never runs for it. `NotificationFanOut` calls `IUnitOfWork.SaveChangesAsync` explicitly; without
  it, every notification is added to a `DbContext` that is disposed with the scope and nothing is
  written — silently, with the outbox row still marked `Processed`. `OrderAuditWriter` sidesteps
  the question by issuing an immediate `INSERT` instead of tracking an entity.
- **`GetAsync` reads untracked; `GetForUpdateAsync` tracks.** Two methods rather than a bool,
  because the distinction decides whether a write happens at all and a caller that gets it wrong
  gets no feedback. `ShipOrder`, `CancelOrder` and `MarkNotificationRead` all mutate what they
  read, so all three take the tracked one.
- **Idempotency is the unique index, not the check.** `(SourceMessageId, UserId)` is unique;
  `ListNotifiedRecipientsAsync` is an optimization in front of it. Delivery is at-least-once, so
  every notifier WILL run twice — two concurrent deliveries can both pass the check, and the index
  is what stops a duplicate landing in someone's feed.
- **The feed is deliberately NOT `ICacheable`**, permanently, for two independent reasons. Nothing
  a caller does creates their own notifications — the outbox pump does, with no current user — so
  there is no command to hang an `IInvalidatesCache` tag off and a cached feed could never be
  evicted. And a cached feed would contradict the realtime push. Same posture as the auth path.
- **Enums cross the wire as names**, via a `JsonStringEnumConverter` registered on **both**
  `AddJsonOptions` (what controllers serialize with) and `ConfigureHttpJsonOptions` (what
  `AddOpenApi`'s schema generator reads). Configuring only the first is the trap: the API sends
  `"OrderPlaced"` while the generated contract still says `type: integer`, so `schema.d.ts` types
  it `number` and every client is wrong in a way no backend test can see.

Realtime push is opt-in (`Realtime__Enabled`, default `false`) and **best-effort by contract** —
the feed is the truth. Above one replica it needs the Redis backplane, because the pod that writes
a notification is whichever one's outbox pump claimed the row and is unrelated to the pod holding
that user's connection; the ingress cookie affinity of ADR 0010 does not help, since the pump is
not serving that user's request. See ADR 0019.

**It is ON in two places, for two different reasons.** In Development
(`src/Api/appsettings.Development.json`) with **no** backplane: a developer's `dotnet run` is a
single process, the one configuration where push cannot reach the wrong replica. And in the
Kubernetes overlay (`k8s/overlays/local/config.yaml`) **with** one —
`Realtime__RedisConnectionString: 'redis:6379'`, against the Redis that `k8s/base/redis.yaml`
deploys for exactly this purpose, because that cluster runs two API replicas and a push must
reach a user whose connection is held by the other pod.

It stays **off** everywhere else: `appsettings.json`'s default, `ApiFactory`, and the Playwright
`webServer` — the last pinned explicitly, since the e2e run sets
`ASPNETCORE_ENVIRONMENT=Development` and would otherwise inherit the dev value.

**Without push the badge is up to thirty seconds stale, by construction.** The outbox pump polls
every second, so the notification row exists almost immediately; `useUnreadCount`'s interval is
the only other thing that would ever ask. That gap is what `frontend/src/features/notifications/
stream.ts` closes — and a `ProductPriceChanged` aimed at someone who took no action has no other
trigger at all, since no client-side invalidation can fire on a user who did nothing. The
frontend needs `/hubs` proxied for any of it to work in dev; see `frontend/CLAUDE.md`.

## Resilience

`Microsoft.Extensions.Http.Resilience` on outbound HTTP clients (`ExchangeRateClient` is the
reference); `EnableRetryOnFailure` on the Npgsql provider for everything else. Both retry
transient faults automatically — a Postgres pod restarting, a third party answering 503 — so
neither should be reached for again by hand.

Three things that will cost you time:

- **Polly cannot see a failed `Result<T>`.** It decides whether to retry by inspecting an
  exception or an `HttpResponseMessage`; a port that has already converted a failure into
  `Result.Failure(...)` reads to Polly as an ordinary, successful return value, so a pipeline
  wrapped around a `Result`-returning call retries nothing — silently, with every registration
  test staying green. The pipeline must sit *under* the port (inside the Infrastructure adapter,
  on the raw transport outcome), never wrapped around it. See `Infrastructure/CLAUDE.md`.
- **An explicit transaction now needs the execution strategy.** `EnableRetryOnFailure` makes a
  bare `context.Database.BeginTransactionAsync()` throw — the strategy cannot retry a block it
  does not own. Use `context.Database.CreateExecutionStrategy().ExecuteAsync(...)` instead.
  Found by `WolverineOutboxAtomicityTests` going red the moment this landed: Wolverine's own EF
  Core outbox integration opens a transaction internally, and needed the same wrapping.
- **Retry is off under test the way the cache is.** `ApiFactory` sets `Resilience:Enabled=false`,
  for the same reason `Cache:Enabled=false` is set: a test asserting an `Unavailable` failure
  must not first sit through the pipeline's own backoff delays. Unlike the cache switch, this one
  does not remove the resilience handler — `AddStandardResilienceHandler` has no supported hook
  to attach conditionally at registration time, before any option is resolvable — it makes the
  retry strategy reject every outcome instead, leaving the timeouts, circuit breaker, and rate
  limiter at their configured values (none of the three has ever tripped in this repo's own
  tests, so none needed neutralising).

See ADR 0014.

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
