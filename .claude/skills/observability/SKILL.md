---
name: observability
description: Use when touching logging, tracing, OTLP export, trace links from the UI into the log store, or the traffic (RED) metrics and their charts - Monitoring__TraceLinkTemplate, ErrorPanel references, Behaviors.LoggedAsync levels, what must never be logged, Observability__Otlp__* config, TrafficMiddleware, histogram percentiles, and traffic_buckets.
---

# Logging, tracing and traffic metrics

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

**Config keys use double underscores, same as every other key:**
`Observability__Otlp__Enabled` and `Observability__Otlp__Endpoint`, never a single underscore,
which binds nothing and warns nothing. `Observability:Otlp:Enabled` defaults to `false`
everywhere — appsettings.json, every test host, CI — so nothing tries to export to a collector
that was never started; the tracer provider itself is still registered unconditionally, which is
what makes `Activity.Current` non-null and the `traceId` already written into every
`ProblemDetails` resolve to a real, correlatable value. `Observability:Otlp:Endpoint` is the OTLP
receiver's **root**, with no `/v1/logs`/`/v1/traces` suffix — `OtlpEndpoint.Build` appends the
right one per signal, and does not rely on the SDK to (confirmed empirically that it will not:
see that method's own remarks for what that cost to discover).

**Every exporter goes through its host's `ConfigureExporter`** (`ObservabilityRegistration` and
`WorkerObservability` each have one — the hosts compose separately, ADR 0016). A new signal's
exporter that sets `Protocol`/`Endpoint` itself will miss the auth headers.

**`Observability__Otlp__Headers` is a secret.** OTLP's `key=value,key2=value2` form, for a hosted
backend's API key (`Authorization=Basic …`). Environment or secret store only — never an
appsettings file. Blank is treated as unset. Seq and the in-cluster collector need none.

**Every record carries `service.version` and `deployment.environment.name`**
(`ObservabilityResource`). The version is the assembly's informational version,
`1.0.0+<commit>`: a local build gets the commit from `.git` via the SDK; the image build has no
`.git`, so `Dockerfile.api` takes `SOURCE_REVISION` and `deploy/deploy.ps1` passes it. An image
built without it reports a bare `1.0.0`.

See `docs/superpowers/plans/2026-09-13-centralized-logging.md` for the full design and the
phased rollout, and ADR 0015 for the decision itself — MEL + a pipeline behavior over Serilog or
a base class, OTLP export over a store-specific sink.


## Trace links

The trace id is how every other record reaches the log store, so the UI hands it out in two
places.

- **Monitoring tables.** Job runs, sign-in attempts and admin actions each have a Trace column
  (`TraceLink`). `Monitoring__TraceLinkTemplate` turns it into a link: a URL with `{traceId}`
  where the 32-hex id goes, carried to the SPA on `GET /api/monitoring/access` (admin-only, so a
  member never learns where the log store is). Unset, the id is selectable text. The API
  **refuses to start** on a template that is not an absolute http(s) URL containing
  `{traceId}` (`MonitoringPageOptions.IsValidTemplate`) — it becomes an `href`.
- **Errors.** `ErrorPanel` renders every failed query and mutation, and for a **5xx only**
  appends `Reference: <trace id>`. A 4xx is the caller's to fix; a reference there reads as
  "contact support".

**Two id shapes, one normaliser.** `ProblemDetails.traceId` is `Activity.Id` — the full W3C
traceparent `00-<trace>-<span>-<flags>`, ASP.NET Core's own convention, pinned by
`ObservabilityRegistrationTests`. The audit tables store the bare 32-hex `TraceId`. Do not
"fix" either: `frontend/src/api/traceId.ts`'s `normaliseTraceId` reads both, and rejects
`HttpContext.TraceIdentifier`'s `0HN…:1` fallback, which appears in no log record.

Verified templates:

| Store | Template |
|---|---|
| Seq (dev, `-WithSeq`) | `http://localhost:55341/#/events?filter=@TraceId%20%3D%20'{traceId}'` |
| OpenSearch Dashboards (kind, `-WithObservability`) | Discover over the `otel-logs` index pattern, `traceId:"{traceId}"`, last 7 days — the full string is in `k8s/components/observability/kustomization.yaml` |

Seq has no separate trace route — traces live in the events view, and `#/events?filter=` is the
shape Seq's own UI links use. OpenSearch Dashboards' link needs an index pattern, which
`dashboards-index-pattern-job.yaml` creates **with its field list** (the saved-objects API does
not fill one in; without it every load raises "Could not locate that index-pattern-field").
It covers logs only: the collector's exporter leaves spans' `@timestamp` at 0001-01-01. Both
templates were checked by loading them in a browser against a real stored trace id.

## Metrics (ADR 0027)

OpenTelemetry metrics for the **runtime and the infrastructure**, beside — never instead of —
the Postgres traffic rollup below, which stays the monitoring page's source.

- **Meters, in both hosts:** HttpClient, `System.Runtime` (`dotnet.gc.*`,
  `dotnet.thread_pool.*`), Npgsql (`db.client.operation.duration`, `db.client.connection.count`
  by state against `db.client.connection.max`), and `Wolverine:*` (`wolverine-execution-time`,
  `wolverine-messages-received`/`-succeeded`, `wolverine-dead-letter-queue`, inbox/outbox counts).
  **API only:** ASP.NET Core's built-in meters (`http.server.request.duration`, Kestrel, auth,
  rate limiting). The worker skips them for the reason it skips ASP.NET tracing.
- **No `OpenTelemetry.Instrumentation.Runtime`.** On .NET 9+ it only subscribes to
  `System.Runtime`, which `AddMeter` does directly.
- **Gate:** exported when `Otlp:Enabled` **and** `Otlp:Metrics` (default `true`). The meter
  provider itself is always registered; `MetricsPipelineTests`/`WorkerMetricsTests` prove the
  meters with an in-memory reader and export off.
- **Where it lands:** Seq 2026.1 (`-WithSeq`) takes OTLP metrics — its Metrics view lists every
  name above. On the cluster, Prometheus + Grafana (see the `kubernetes` skill).
- **Cardinality is a review item.** Every tag here is a route template, a pool or a queue name.
  A tag carrying a user id, an order id or a raw path multiplies the series count.

## Traffic

RED metrics — rate, errors, duration — for both the HTTP surface and every command and query,
recorded per pod in memory and flushed to `traffic_buckets` on a minute boundary. One row per
`(BucketStart, Kind, Name, InstanceId)`, never one row per request. `/monitoring/traffic` sums
across instances, which is what makes two API replicas one number rather than whichever pod
answered.

`TrafficMiddleware` keys HTTP on `"{method} {route template}"`, taken off the matched endpoint
**after** `next()` has run — a raw path would give one row per order id, and routing has not
matched an endpoint yet on the way in. Commands and queries need no new instrumentation at all:
`Behaviors.LoggedAsync` already computes the name, the outcome and the elapsed milliseconds, so
the recorder is fed from there.

Five things that will cost you time:

- **Percentiles come from a fixed histogram, and a mean is not a substitute.**
  `TrafficHistogram.Bounds` is `5/10/25/50/100/250/500/1000/2500/5000`ms plus an overflow bucket,
  and p50/p95/p99 are interpolated from the summed counts. That summing is the whole point: counts
  from two pods add, so the answer is the same as if one pod had done all the work. **Per-pod
  means cannot be combined into a percentile, or into anything.** The bounds are a stored
  contract — rows already written were counted against them, so changing one silently rewrites
  history rather than improving it.
- **`ITrafficRecorder` is resolved with `GetService`, not `GetRequiredService`**, exactly as
  `LoggedAsync` already resolves `ICurrentUser`. The outbox pumps dispatch with no recorder in
  scope and that is normal, not a wiring error.
- **The flush is an upsert and must stay one.** `ON CONFLICT ... DO UPDATE SET col =
  EXCLUDED.col` *assigns* rather than adds, so a retried flush of the same closed bucket is
  idempotent. `TakeClosedBuckets` hands over only minutes that have ended; a pod that dies
  mid-bucket loses at most its own last minute, which is the accepted price of not writing a row
  per request.
- **The worker records too, under its own `InstanceId`.** Without it every command a job runs is
  invisible. Same recorder, same hosted service, different instance — and that is also why clock
  skew between hosts is accepted rather than corrected: the bucket boundary is `IClock` truncated
  to the minute, and nothing here may depend on sub-minute precision.
- **Traffic is pruned at seven days**, not thirty like `sign_in_events`
  (`Monitoring__TrafficRetentionDays`). It is the highest-volume table in the application and
  carries no personal data, so the trade runs the other way.

The charts are hand-rolled inline SVG — no chart library is installed, and the `dataviz` skill
governs the palette and the chart forms. **Never a dual-axis chart:** requests and errors share a
unit and one axis, latency is milliseconds and gets its own chart.

See ADR 0021.

