---
name: kubernetes
description: Use when deploying to or debugging the local kind cluster - deploy.ps1, two API replicas across two nodes, HPAs and disruption budgets, ingress cookie affinity, TLS, the migration Job, -WithObservability (OTel Collector to OpenSearch), and e2e-k8s.ps1.
---

# Running on Kubernetes

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

## `-WithObservability`

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

