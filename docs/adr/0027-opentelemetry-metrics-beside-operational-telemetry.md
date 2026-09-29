# 0027. OpenTelemetry metrics beside operational telemetry

**Date:** 2026-09-29
**Status:** Accepted

> Written ahead of the cluster half of the implementation, like ADR 0021, and implemented the
> same day by `docs/superpowers/plans/2026-09-29-opentelemetry-observability.md` (Tasks 9, 10
> and 12). The metric and label names the dashboard and alert rules use were read off a live
> Prometheus on the kind cluster, not taken from documentation. Two things the cluster taught
> that this ADR did not foresee are recorded in the `kubernetes` skill: a counter born at 1 needs
> Prometheus's `created-timestamp-zero-ingestion`, and Grafana's rate windows need the 60s push
> interval declared.

## Context

ADR 0015 built logs and traces over OTLP and left metrics explicitly out of scope: "the OTel
packages make `.WithMetrics()` a few lines away, but this is a logging plan." ADR 0021 then
answered the monitoring page's need for rate, errors and duration without a metrics pipeline, by
recording **operational facts** in Postgres (`traffic_buckets`, `job_runs`, `sign_in_events`),
because the OpenSearch stack is absent in almost every environment the page is built and tested
in. That ADR's context recorded the gap plainly: "There is no metrics pipeline whatsoever."

Three things now need what `traffic_buckets` cannot give:

1. **The runtime and its dependencies are invisible.** GC pauses, thread-pool starvation, the
   Npgsql connection pool, Wolverine's execution time and dead-letter count, RabbitMQ queue depth.
   None is an operational fact about the application's domain; all are what an operator needs
   when the monitoring page says "p95 went up" and does not say why. The pool matters in
   particular: a small managed Postgres (Azure's B1ms allows roughly fifty connections) is
   exhausted long before CPU is.
2. **Nothing can alert.** `traffic_buckets` is read by a page a person opens. A rule that fires
   when the 5xx rate crosses 5% needs a store that evaluates rules on its own.
3. **A hosted backend was always the plan for production.** ADR 0015 chose OTLP so the store is a
   configuration choice; every hosted backend (Grafana Cloud, Azure Monitor, Honeycomb) takes OTLP
   metrics on the same pipe as logs and traces.

`OpenTelemetry.Instrumentation.Runtime` has been referenced in `src/Api` since ADR 0015 and never
wired. On .NET 9 and later it only subscribes to the runtime's built-in `System.Runtime` meter.

## Decision

**Export OpenTelemetry metrics from both hosts over the existing OTLP pipe, and keep ADR 0021's
Postgres tables as the source of the monitoring page.** The two answer different questions and
neither replaces the other.

- **What is measured, in the app:** ASP.NET Core's built-in meters (API only — the worker's HTTP
  surface is two probes), HttpClient, `System.Runtime`, Npgsql (`AddNpgsqlInstrumentation`), and
  Wolverine (`Wolverine:*`). No application code records a metric by hand; `traffic_buckets`
  still does that job for the page.
- **What is measured, outside the app:** RabbitMQ and Postgres, by the collector's `rabbitmq` and
  `postgresql` receivers. The broker's queue depth is the broker's to report; the app cannot see
  another consumer's queue, and a metric that disappears when the app is down is useless for
  noticing that the app is down.
- **The gate:** exported only when `Observability:Otlp:Enabled` and the new
  `Observability:Otlp:Metrics` (default `true`) are both true. `Enabled` stays `false` by default
  everywhere, so a plain `dotnet run`, CI and every test host export nothing — the same default,
  for the same reason, as ADR 0015's logs and traces.
- **Where it lands:** in dev, Seq (2026.1 ingests OTLP metrics, so `-WithSeq` needs no new
  container). On the cluster, behind the existing opt-in observability component: the collector
  forwards to **Prometheus through its native OTLP receiver** — pushed, never scraped, so no pod
  opens a metrics port — and **Grafana** shows them with a provisioned dashboard. Prometheus also
  evaluates the **alert rules**. OpenSearch keeps logs and traces and nothing else.
- **The unused `OpenTelemetry.Instrumentation.Runtime` reference is removed** in favour of
  `AddMeter("System.Runtime")`, which is all it did on this runtime.

## Consequences

- **Two sources of request rate and latency exist, and they will not agree exactly.**
  `traffic_buckets` counts per route template per minute with this application's own histogram
  bounds (5 ms to 5 s); `http.server.request.duration` uses ASP.NET Core's own bucket bounds, and Prometheus
  computes quantiles over whatever `rate()` window a query picks. A p95 that differs by a bucket between the monitoring page and
  Grafana is expected, not a bug. The page remains the reference for "what is the application
  doing"; Grafana for "why".
- **The opt-in cluster component gets heavier.** Prometheus and Grafana join OpenSearch and its
  Dashboards behind `deploy.ps1 -WithObservability`. A plain `deploy.ps1` is unchanged.
- **Metric and label names become a dependency.** Dashboards and alert rules key on OpenTelemetry
  semantic-convention names as Prometheus translates them (`http_server_request_duration_seconds`,
  `db_client_connection_count`, …). A package upgrade that moves a semantic convention can blank a
  panel or silence an alert without failing a build. They were read from a live Prometheus, not
  written from documentation, and the dashboard is the first place to look after an upgrade.
- **Cardinality is now a production concern.** ASP.NET Core tags `http.route` with the route
  template, not the path, so `/api/orders/{id}` is one series — the same rule `TrafficMiddleware`
  follows. Anything that adds a tag with a user id, an order id or a raw path would multiply the
  series count; that is a review item for every new meter.
- **Metrics are cheap to keep on.** The SDK aggregates in process and exports every 60 seconds by
  default; there is no per-request export.
- **Rules out:** a `/metrics` scrape endpoint on the API or the worker (see below), and replacing
  `traffic_buckets` with a query against Prometheus.

## Alternatives considered

- **A Prometheus scrape endpoint in each host** (`OpenTelemetry.Exporter.Prometheus.AspNetCore`).
  Lost because it is a second export path beside OTLP, with its own port on every pod, its own
  scrape configuration, and no equivalent for a hosted backend. ADR 0015's point was one pipe
  whose destination is configuration.
- **OpenSearch for metrics.** Lost because the collector's OpenSearch exporter writes logs and
  traces, OpenSearch has no PromQL, and its alerting is a separate plugin with its own model. It
  would have avoided one component and made every dashboard and rule harder.
- **Replacing `traffic_buckets` with OTel metrics.** Lost for exactly ADR 0021's reason: the
  monitoring page must work in every environment, including those with no metrics store, and it
  must sum two replicas into one number without depending on the store that is usually absent.
- **The Azure Monitor OpenTelemetry distro** (`Azure.Monitor.OpenTelemetry.AspNetCore`). Lost
  because it hard-codes the vendor into both hosts. Azure Monitor accepts OTLP through the
  Container Apps OpenTelemetry agent, so the vendor stays a configuration choice.
- **Doing nothing until production.** Lost because alert rules and dashboards written without a
  live store to check them against are guesses about metric names, and the kind cluster is where
  every other piece of this system is rehearsed before it is trusted.
