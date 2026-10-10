---
name: kubernetes
description: Use when deploying to or debugging the local kind cluster - deploy.ps1, two API replicas across two nodes, HPAs and disruption budgets, ingress cookie affinity, TLS, the migration Job, -WithObservability (OTel Collector to OpenSearch for logs and traces, Prometheus and Grafana for metrics, tail sampling, alert rules and their promtool tests), and e2e-k8s.ps1.
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

RabbitMQ (`k8s/base/rabbitmq.yaml`) runs as a StatefulSet for the same reason Postgres does: its
PVC is bound to whichever node it first scheduled on, by kind's node-local storage class, so a
drain of that node will not reschedule it either — target a different node for a drain test, as
the Postgres note above already says. `deploy.ps1` waits on `rollout status statefulset/rabbitmq`
immediately after the Postgres wait and before Phase B's migrations, because both hosts refuse to
start without a broker connection (ADR 0026) and letting the rollout race ahead would only buy
CrashLoopBackOff. The management UI is reachable with `kubectl port-forward svc/rabbitmq 15672`
and deliberately not exposed on the ingress — it is an operator tool for this local cluster, not
something the application depends on. Neither host's `/health/ready` checks RabbitMQ: readiness
calls `AddDbContextCheck`'s Postgres probe (and, in the worker, Quartz's own check), so a broker
outage does not flip a pod's readiness the way a Postgres outage does. External systems' checks
are registered in both hosts but filtered out of `/health/ready` by
`ExternalSystemHealth.IsNotExternal`, for the same reason: a partner outage must never take a pod
out of rotation.

**External-system secrets are not in the cluster yet.** ADR 0031 fixes the contract a real
environment's Vault Secrets Operator must meet — one Secret per system mounted at
`/var/run/secrets/external-systems/<system>/`, `rolloutRestartTargets` naming the api and worker
Deployments — but the local overlay mounts none and configures no system, so nothing here calls
one.

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
- **Metrics go to Prometheus, pushed** (ADR 0027). The collector's `metrics` pipeline takes the
  apps' OTLP plus its own `rabbitmq` (management API, port 15672) and `postgresql` receivers,
  whose credentials come from `app-secrets` through the collector's `env`, and exports to
  Prometheus's native OTLP receiver (`--web.enable-otlp-receiver`). No pod exposes a metrics
  port and Prometheus has no scrape config. `otlp.promote_resource_attributes` in
  `prometheus.yaml` is what turns `service.version`, `deployment.environment.name` and the
  receivers' queue/database names into labels — anything not listed stays on `target_info`.
- **Metric names are Prometheus's translation, and some are ugly.** Units are appended, so
  Wolverine's non-standard `Messages`/`Milliseconds` units produce
  `wolverine_inbox_count_Messages` — capital included. Read names off
  `/api/v1/label/__name__/values` unfiltered; a `[a-z_]` filter hides these.
- **Grafana** (`grafana.yaml`) has one provisioned datasource and one dashboard, and anonymous
  Viewer access. The dashboard is `grafana-dashboard.json`, a real file turned into a ConfigMap
  by the component's `configMapGenerator` — edit the JSON, not a YAML string.
- **None of the three reloads mounted config, and they are handled differently on purpose.**
  `deploy.ps1` restarts the collector and Grafana after applying (both stateless). Prometheus is
  **not** restarted: its storage is an emptyDir, and a restart per deploy wiped the history a
  deploy is meant to be compared against. Instead `prometheus.yml` and `prometheus-rules.yml`
  are generated ConfigMaps **with** kustomize's content-hash suffix, so a changed file renames
  the ConfigMap, the Deployment changes, and the pod rolls — only then. Do not add
  `disableNameSuffixHash` to those two: a changed rule would silently never take effect.
- **Checking a collector change without a cluster:** extract `config.yaml` from the ConfigMap
  and run the pinned image with `validate --config=…`; it names the broken component. The same
  goes for `promtool check config`/`check rules` in the Prometheus image.
- **Tail sampling lives in the collector's traces pipeline**: every ERROR trace, every trace over
  1s, and 20% of the rest (measured: 39 of 200 fast requests kept; the dead-lettered shipment's
  error trace kept). It needs **one collector replica** — a trace's spans must meet in one
  collector. Logs are never sampled.
- **Alert rules are `prometheus-rules.yml`, unit-tested by `prometheus-rules.test.yml`**, run
  with the pinned image's promtool:
  `docker run --rm --entrypoint promtool -v "<abs path>/k8s/components/observability:/rules" prom/prometheus:v3.15.0 test rules /rules/prometheus-rules.test.yml`.
  Eight rules: 5xx share, p95 latency (with a traffic guard), dead letters, broker backlog, pool
  saturation, `TelemetryMissing` (which notices when the others have gone quiet for the
  wrong reason), `ExternalSystemFailing` (over 20% of a system's calls faulted in 5 minutes, at
  least 10 calls) and `ExternalSystemCertificateExpiring` (under 14 days, `min` across pods). No Alertmanager: firing alerts show at Prometheus's `/alerts`.
- **`--enable-feature=created-timestamp-zero-ingestion` is load-bearing.** A counter series born
  at 1 (the first dead letter, the first 5xx on a route) is otherwise invisible to
  `increase()`/`rate()`, and `MessagesDeadLettered` stays silent for exactly the dead letter it
  exists for.
- **Grafana's datasource carries `timeInterval: 60s`** — the OTLP push interval. At the 15s
  default, `$__rate_interval` holds one sample and every rate panel says "No data".
- Port-forwards (printed by `deploy.ps1`): Grafana 3000, Prometheus 9090 (`/alerts`), OpenSearch
  Dashboards 5601. A pod restart breaks an open port-forward silently; start a new one.

