# Running AIFrameWork on Kubernetes

Date: 2026-09-09
Status: Approved, not yet implemented

## What this is

A local Kubernetes cluster that runs the whole application — API, SPA, and Postgres —
with the API on **two replicas**. The goal is not "the app starts in a pod". It is to
rehearse a real deployment closely enough that the things which only break at more than
one replica break *here*, where they are cheap.

Nothing in the repository is containerized today. There is no Dockerfile. So this spec
covers packaging as well as orchestration.

`docker-compose` is untouched and remains the primary inner development loop and the
backing store for the e2e suite. This work is additive.

## Decisions

| Decision | Choice | Rejected |
|---|---|---|
| Cluster | Local (kind) | Cloud managed, on-prem |
| API replicas | 2 | 1 |
| Shared state store | Postgres only | Redis |
| SPA delivery | Separate nginx pod, ingress path-routes | Served from API `wwwroot`; left out of cluster |
| Manifests | Kustomize, `base` + `local` overlay | Helm, plain YAML |
| Cache eviction across pods | Ingress session affinity | Redis L2, disable cache, `LISTEN`/`NOTIFY` |

Kustomize was chosen over Helm because there is exactly one environment. Helm's advantage
here was never templating — it was the `pre-upgrade` hook, which is the natural primitive
for "migrations must finish before any pod starts". Kustomize has no hook concept, so that
ordering moves into an explicit deploy script. That script is the acknowledged price of
this choice, and it is five steps long.

## Three things that are per-process today

The application holds three pieces of state in memory. At one replica this is invisible.
At two it is not, and each needs a decision rather than a discovery.

| Component | Behaviour at 2 replicas | Resolution |
|---|---|---|
| Cookie auth (`Program.cs:35`) | **Breaks.** No Data Protection key persistence is registered, so each pod generates an ephemeral key ring and rejects cookies minted by the other. Users get seemingly random 401s. A single pod restart also signs everyone out. | Fixed — persist the key ring to Postgres (§1.1) |
| `HybridCache` (`CachingRegistration.cs:36`) | Eviction stops working across pods. Caching itself is unaffected. | Ingress session affinity (§3.3) |
| Auth rate limiter (`Program.cs:83`) | Effective limit becomes N × `PermitLimit` per window. | Accepted (see Accepted trade-offs) |

The outbox needs nothing. `OutboxPoller.ClaimAsync` claims with `FOR UPDATE SKIP LOCKED`,
so concurrent pollers across pods were designed for from the start.

### Why the cache case is narrower than it looks

The failure is specific and worth stating exactly, because "the cache breaks" is wrong.

1. `GET /api/orders` → pod A → miss → database → A caches `GetOrders:{userId}` for 30s.
2. `POST /api/orders` → pod B → commits → `EvictAsync` calls `RemoveByTagAsync` against
   **B's** memory. A's entry is untouched; B has no way to reach it.
3. The refetch, milliseconds later → pod A → **cache hit, stale**. The order the user just
   placed is missing from the list.

This is precisely the guarantee `ICacheable`'s own documentation says the synchronous
eviction path exists to provide: "the frontend refetches milliseconds after a 201, and must
not be served a page that omits what it just created."

It is not a rare race. The ingress pools upstream connections and spreads requests, so with
two replicas the write and the follow-up read landing on different pods is roughly a coin
flip.

What survives intact: keys are composed with `ICurrentUser.Id` (`CacheScope.cs:13`), so the
blast radius is strictly the acting user seeing their own stale view. There is no
cross-user leakage, by construction. And it self-heals within the 30s TTL.

`Behaviors.cs` names the assumption out loud — *"With an L1-only HybridCache
RemoveByTagAsync has no realistic failure mode"*. ADR 0009 was written on a
single-process premise. This work invalidates that premise, which is why it needs an ADR
rather than a code comment.

## 1. Changes to the application

### 1.1 Data Protection key ring → Postgres

Mandatory. This is the change that, if skipped, produces a bug that looks like a flaky
login and is miserable to diagnose.

- Add `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` to
  `AiFramework.Infrastructure.csproj`, version-aligned with the other 10.0.11 packages.
  Permitted by the dependency rule: `dependency-rule.ps1:64` bans only
  `AiFramework.Api` in the `Infrastructure` layer.
- `AiFrameworkDbContext` implements `IDataProtectionKeyContext` and gains
  `DbSet<DataProtectionKey> DataProtectionKeys`. It belongs beside `Outbox` and
  `OrderAudits`, which are already infrastructure-owned tables in that context.
- One new migration creating the table. Append-only — a new migration, never an edit to an
  existing one.
- Registration in `Program.cs`, beside the other explicit wiring:

  ```csharp
  builder.Services.AddDataProtection()
      .PersistKeysToDbContext<AiFrameworkDbContext>()
      .SetApplicationName("AiFramework");
  ```

`SetApplicationName` is load-bearing, not decoration. The purpose string is derived from
it, so two pods that disagree on the application name will read the same key ring and
*still* refuse each other's cookies — the same symptom, with the fix apparently already
applied.

**Test.** An integration test that issues a cookie from one host and asserts a second host,
built over the same Postgres container, accepts it. Two `WebApplicationFactory` instances
sharing the existing `PostgresFixture`. This is the only thing that actually guards the
regression; asserting the table exists proves nothing.

### 1.2 A readiness probe, separate from `/health`

`/health` stays exactly as it is and keeps its current meaning.

`HealthTests` runs with no database at all — connection string `""`, `Wolverine:Durable=false` —
and asserts 200. It is deliberately container-free, and `tests/CLAUDE.md` documents that it
sits outside the shared collection for this reason. Adding a database check to `/health`
breaks both the test and its stated intent.

A second endpoint instead:

| Probe | Path | Checks |
|---|---|---|
| `livenessProbe` | `/health` | Process is alive. Unchanged, touches no database. |
| `readinessProbe` | `/health/ready` | New. `AddDbContextCheck<AiFrameworkDbContext>()`. Withholds traffic until Postgres answers. |
| `startupProbe` | `/health` | Generous `failureThreshold`. Durable Wolverine does schema work at boot; a slow start must not read as a crash loop. |

Adds `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`. Both endpoints
stay anonymous — no fallback authorization policy is registered, so neither needs an
attribute, consistent with the existing note at `Program.cs:167`.

### 1.3 Graceful shutdown

Manifest configuration, not code.

`OutboxPoller.ClaimAsync` increments `Attempts` at claim time rather than on failure. A
`SIGKILL` mid-handler therefore burns an attempt against `MaxAttempts`. With rolling
updates across two replicas, pod termination is routine, so this stops being theoretical.

The defaults are a trap: ASP.NET Core's host shutdown timeout is 30s and Kubernetes'
`terminationGracePeriodSeconds` default is *also* 30s. A host that uses its full budget is
`SIGKILL`ed exactly at the boundary.

- `terminationGracePeriodSeconds: 60`, against the 30s host timeout.
- A `preStop` sleep of ~5s, so the pod is removed from Service endpoints before it stops
  accepting connections. Without it, in-flight requests are refused during a rolling update
  while endpoint removal propagates.

## 2. Images

### 2.1 `aiframework-api`

Multi-stage. `mcr.microsoft.com/dotnet/sdk:10.0` restores and publishes; the runtime stage
is `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` — non-root by default, no shell.

Two hard constraints:

- **Release, always.** Release is what loads the pre-generated Wolverine adapters committed
  under `src/Api/Internal/Generated`. A Debug container would run fine locally and diverge
  from what CI validates, which is the exact failure mode the codegen rules exist to
  prevent.
- **A `.dockerignore` is mandatory**, not hygiene. Without it, local `bin/` and `obj/` are
  copied into the build context and can poison the restore, and `node_modules` makes the
  context enormous.

`TreatWarningsAsErrors` is set across compiler, analyzers, and build, so the image build
fails on anything CI would fail on. That is desirable; do not weaken it in the Dockerfile.

If the chiseled base turns out to lack an ICU dependency this code needs, fall back to
`aspnet:10.0-alpine` rather than setting `InvariantGlobalization`, which would change
runtime behaviour to solve a packaging problem.

### 2.2 `aiframework-web`

`node:24-alpine` runs `npm ci && npm run build`; `nginxinc/nginx-unprivileged:alpine`
serves the output on 8080.

- `npm run build` is `tsc -b && vite build`, so type errors fail the image build.
- nginx needs `try_files $uri $uri/ /index.html;`. Without it a browser refresh on a
  client-side route such as `/orders` returns 404 instead of the SPA.
- `nginx-unprivileged` rather than `nginx:alpine`, which runs as root to bind port 80.

**No frontend code changes are required.** `frontend/src/api/client.ts` issues relative
paths and relies on `fetch`'s default `same-origin` credential mode, and documents that
switching to `'include'` "would be the change needed if the API ever moved to its own
origin." Ingress path-routing keeps everything on one origin, so that change is not needed
and must not be made.

### 2.3 Migration bundle

A third stage in the API Dockerfile, built with `--target migrator`. It carries the output
of `dotnet ef migrations bundle`, a self-contained executable.

This is why a bundle rather than `dotnet ef database update` in the Job: the bundle keeps
the EF Design package out of every runtime image. CLAUDE.md records what that package costs
when it leaks into a publish — 7.9MB to 37MB. Keeping the bundle in the same Dockerfile
holds it in version lockstep with the application it migrates.

The bundle is built with `src/Infrastructure` as both `--project` and `--startup-project`,
matching the repository's existing `dotnet ef` invocation. It receives its connection string
explicitly via `--connection` rather than relying on `DesignTimeDbContextFactory`'s
environment variable, so the Job's behaviour is visible in the manifest.

## 3. Cluster topology

Namespace: `aiframework`.

| Object | Shape |
|---|---|
| `postgres` | StatefulSet, 1 replica, `postgres:17-alpine` (matching `docker-compose.yml`), 2Gi PVC, headless Service |
| `api` | Deployment, **2 replicas**, probes per §1.2, `terminationGracePeriodSeconds: 60`, preStop sleep, non-root `securityContext`, resource requests and limits |
| `web` | Deployment, 2 replicas, nginx-unprivileged, HTTP probe on `/` |
| `migrate` | Job, runs the §2.3 bundle to completion |
| `ingress` | ingress-nginx, one host, TLS, path-routed |

### 3.1 Host and TLS

Host is `aiframework.localtest.me`. That name resolves to `127.0.0.1` publicly, so **no
`/etc/hosts` editing is required** on any machine.

TLS is not optional here, and the reason is a trap worth stating plainly:
`ASPNETCORE_ENVIRONMENT=Production` sets `CookieSecurePolicy.Always` (`Program.cs:48`). Over
plain HTTP the browser silently discards the session cookie — login appears to succeed and
every subsequent request returns 401, with nothing in the application logs. The alternative,
running the cluster under a non-Production environment name, is rejected: it also changes
OpenAPI exposure (`Program.cs:174`) and error detail, so the rehearsal would stop rehearsing.

A self-signed certificate in a Secret is sufficient locally.

### 3.2 Routing

Under the single host: `/api` → the `api` Service, `/` → the `web` Service. One origin is
what keeps `SameSite=Lax` and `same-origin` credentials working without touching the
frontend.

### 3.3 Session affinity

```yaml
nginx.ingress.kubernetes.io/affinity: "cookie"
nginx.ingress.kubernetes.io/session-cookie-name: "aiframework.route"
```

This is the resolution for the cache-eviction problem described above. Pinning a user to a
pod means the pod that evicts is always the pod that reads, restoring read-your-own-writes.

The residual risk is smaller than it first appears. If a pod dies and a user is rebalanced,
the new pod holds *no* entry for them — affinity meant they were never served there — so
they take a cold miss and read the database. Correct, merely slower. Serving a stale entry
would require two rebalances inside the same 30-second window.

### 3.4 Configuration

| Source | Keys |
|---|---|
| ConfigMap | `ASPNETCORE_ENVIRONMENT=Production`, `Cache__Enabled=true`, `Wolverine__Durable=true`, `RateLimiting__Auth__*` |
| Secret | `ConnectionStrings__Default`, `POSTGRES_PASSWORD` |

Note the **double** underscore for nested configuration keys. A single underscore does not
bind, does not warn, and leaves the default value in place — the failure is silent.

## 4. Deploy flow

Kustomize has no hook mechanism, so ordering lives in `deploy/deploy.ps1` — PowerShell, to
match the existing `.claude/hooks` and the development platform.

1. `kubectl apply -k k8s/overlays/local` for namespace, Secret, ConfigMap, Postgres.
2. Wait for Postgres to report ready.
3. `kubectl delete job migrate --ignore-not-found`, then apply the Job, then
   `kubectl wait --for=condition=complete`.
4. Apply `api`, `web`, and the Ingress.

Step 3 deletes before applying deliberately. A completed Job has immutable fields, so a
plain re-`apply` on the second deployment fails.

`deploy/kind-cluster.yaml` maps host ports 80 and 443 into the cluster and labels the node
`ingress-ready=true` so ingress-nginx schedules onto it.

## 5. File layout

```
Dockerfile.api            # build, migrator, and runtime stages
Dockerfile.web
.dockerignore
k8s/
  base/
    namespace.yaml
    postgres.yaml         # StatefulSet, headless Service, PVC
    api.yaml              # Deployment, Service
    web.yaml              # Deployment, Service
    migrate-job.yaml
    ingress.yaml
    kustomization.yaml
  overlays/
    local/
      kustomization.yaml
      config.yaml         # ConfigMap
      secret.yaml
      tls.yaml
deploy/
  kind-cluster.yaml
  deploy.ps1
```

## Open risk to verify first

**Concurrent Wolverine schema migration.** Durable Wolverine migrates its own envelope
tables during host startup, and with two replicas both pods do this against the same
database at the same time. Wolverine is believed to guard this with advisory locks, but
that has not been verified and must not be assumed.

This is the first thing the implementation plan resolves, because it has a design
consequence: if the guard does not hold, Wolverine's automatic schema migration is disabled
and its schema is folded into the migrate Job from §2.3, alongside the EF bundle.

## Accepted trade-offs

- **Data Protection keys are stored unencrypted at rest** in Postgres. Consistent with the
  judgement already applied to the committed dev connection string, and acceptable for a
  local cluster with throwaway credentials. A cloud target requires `ProtectKeysWith*`, and
  ADR 0010 records that as the condition on this decision rather than leaving it implied.
- **The auth rate limiter permits N × `PermitLimit`** across N replicas, because partitions
  are per-process. This is a volume-defence loss, not a correctness one: ADR 0008's
  per-account lockout lives in the database and is unaffected. Moving the limit to the
  ingress is the exit if it matters, and is deliberately not done now.
- **Cache correctness depends on ingress affinity.** This is the substantive change to ADR
  0009's premise. Redis L2 is the principled exit; it was declined here to avoid adding a
  stateful dependency, and because HybridCache's tag-based eviction — the entire mechanism
  ADR 0009 relies on — had incomplete L2 support in .NET 9 and its .NET 10 status would need
  verifying before it could be trusted.

## Out of scope

- Any cloud cluster, managed Postgres, or cloud secret store.
- Redis.
- Changes to `docker-compose.yml`, `docker-compose.e2e.yml`, the e2e suite, or the
  documented local development workflow.
- CI building or publishing images. CI is unchanged by this work.
- Horizontal autoscaling, PodDisruptionBudgets, NetworkPolicies, observability stack.

## Done when

- `deploy/deploy.ps1` takes a fresh kind cluster to a working application.
- Signing in through the ingress works, and the session survives being served by either API
  pod — verified by deleting one pod mid-session.
- Placing an order and immediately reloading the list shows the new order.
- `kubectl rollout restart deployment/api` completes with no failed requests.
- The two-host cookie integration test from §1.1 passes.
- ADR 0010 is written, recording the affinity dependency and amending ADR 0009's premise.
