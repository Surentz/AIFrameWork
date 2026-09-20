# 0021. Operational telemetry in Postgres

**Date:** 2026-09-20
**Status:** Accepted

> **Written ahead of the implementation**, like ADR 0020 and unlike ADR 0011. The decision is
> made; the code lands across phases 2–4 of
> `docs/superpowers/plans/2026-09-20-monitoring-page.md`. Phase 2's first task is a spike into how
> Wolverine surfaces a handler's outcome to middleware, and its result may change *where* the job
> recorder attaches — it does not change anything decided here.

## Context

ADR 0015 built the logging story: every command and query logged by a pipeline behavior, exported
over OTLP to a collector, landing in OpenSearch. It is a good diagnostic store and this ADR does
not disturb it.

It is also, in almost every environment this application runs in, **not there**.
`Observability:Otlp:Enabled` defaults to `false` in `appsettings.json`, in every test host, and in
CI — deliberately, so that "a developer with no collector running, and every CI job, still gets a
green build." The OpenSearch stack itself is opt-in behind `deploy.ps1 -WithObservability`, which
the root `CLAUDE.md` describes as "the largest thing in the cluster by a wide margin," to be
reached for "only when the logging pipeline itself is what you're rehearsing." A plain
`./scripts/dev.ps1`, the e2e stack, CI, and a plain `deploy.ps1` run all have **no log store at
all**.

So a monitoring page that reads its data from OpenSearch would be blind in every environment where
it is being built and tested, and would promote the heaviest optional component in the cluster to
a hard dependency of a page people are meant to open when things are going wrong.

Three more gaps made the choice sharper:

1. **There is no metrics pipeline whatsoever.** No `Meter`, no Prometheus, no counters. Traces and
   logs are the only telemetry that exists. "Request rate, error rate, latency" has nothing to
   read from today.
2. **Job outcomes are not recorded anywhere queryable.** ADR 0017 established this while choosing
   Quartz, and was explicit that its own history is not the answer: "Quartz's own execution
   history would record only 'fired,' never the job's outcome, so it is not what run history will
   read." It named the alternative in the same breath — run history "built once, at the Wolverine
   layer, and see every run," whether the trigger is a schedule, a domain event, or a button.
3. **Sign-ins leave almost no trace.** `User` carries `FailedSignInAttempts` and `LockedOutUntil`,
   which are lockout state rather than history. Who signed in, from where, and which attempts
   failed against accounts that do not exist, exist only as log lines in a store that is usually
   absent.

## Decision

**Operational facts are domain data. They are persisted in Postgres, in application-owned tables,
and read through the ordinary Clean Architecture query pipeline.**

The distinction that carries this decision is between **operational facts** and **diagnostic
detail**:

| | Operational facts | Diagnostic detail |
|---|---|---|
| Examples | A job ran and failed; a sign-in was refused; 1,204 requests hit `/api/orders` this minute | The log lines that job emitted; the spans of that request |
| Shape | Structured, bounded, known columns | Free text, high cardinality, high volume |
| Access | *Queried* — filtered, sorted, aggregated, joined to domain entities | *Searched* — grep across a corpus |
| Store | **Postgres** (this ADR) | **OpenSearch** (ADR 0015) |

The two are joined by `TraceId`, which every operational row carries. Where a log store is
running, the page is one deep link from the full record. Where it is not, the page still works and
says what happened; it simply cannot show the log lines, which is the correct degradation.

Four tables, all EF-mapped and application-owned: `job_runs`, `sign_in_events`, `traffic_buckets`,
and `LastSeenAt` as a column on `User`. Their columns are specified in the plan.

### Traffic is rolled up per pod, per minute, and summed at read

The API runs at two replicas. In-memory counters are therefore per-pod, and under the ingress
cookie affinity of ADR 0010 — which ADR 0018 confirms "does not rebalance anyone already
connected" — an operator would see the numbers from whichever pod their own session is pinned to,
consistently, forever. Not a sample: one pod's truth presented as the application's.

So each pod accumulates in memory over a one-minute bucket and flushes **one row per (bucket,
kind, name, instance)**, tagged with its `HOSTNAME` — the same value
`ObservabilityRegistration` already passes as `serviceInstanceId`. Reads sum across instances.

**Latency is stored as fixed histogram buckets, not as an average.** This is not a refinement, it
is what makes the rollup work at all: means cannot be combined across pods into a percentile, and
p95 is the number anyone actually wants. A histogram sums.

**HTTP traffic is keyed on the endpoint's route template, never the raw path.** `/api/orders/{id}`
is one row; the raw path would be one row per order id, per minute, per pod.

**The command and query side needs no new timing.** `Behaviors.LoggedAsync` already computes the
request type's name, the outcome, and the elapsed milliseconds for every dispatch. The recorder is
resolved there with `GetService`, not `GetRequiredService`, exactly as that method already
resolves `ICurrentUser` — the outbox pumps dispatch with no recorder registered, and that is
normal rather than a wiring error.

### Job runs and sign-in events are written with immediate SQL

Both follow `OrderAuditWriter`'s precedent — an `ExecuteSqlInterpolatedAsync` insert, not a
tracked entity — for two independent reasons. The record must survive the handler's own
transaction rolling back, because a job run that failed is precisely the one worth recording. And
a middleware that called `SaveChangesAsync` would commit the handler's half-finished work as a
side effect of recording that it had started.

### Wolverine's own tables are read, never owned

Dead letters are read from the `wolverine` schema by raw SQL. Wolverine is the schema authority
there — `WolverineEventPath` puts them in their own schema precisely so that "two schema
authorities" do not argue over `public` — and nothing in this application may map them as EF
entities or touch them in a migration.

### Retention is part of the feature

Sign-in events and job runs are pruned at 30 days, traffic buckets at 7, by scheduled jobs
declared with `JobDescriptor.Scheduled<T>` exactly as `PruneProcessedOutbox` already is (ADR
0017). The windows are configuration with the defaults committed. Sign-in events carry IP address
and user-agent, which makes that table personal data, which makes its pruner a requirement rather
than housekeeping.

### Nothing here is cacheable

No monitoring query implements `ICacheable`, permanently. Two reasons, either sufficient:
monitoring data is the current state of the system, and a thirty-second stale answer to "is it
broken right now" is worse than no answer. And ADR 0009's cache is caller-scoped by construction —
`CacheScope` prepends `ICurrentUser.Id` — so global operational data would be cached once per
administrator and evicted by nothing, since no command a user issues creates these rows. That is
the same reasoning that keeps the notification feed uncached.

## Consequences

**Monitoring data shares a failure domain with the thing it monitors.** This is the largest cost
and it deserves to be stated first: when Postgres is down, the monitoring page is down, and that
is exactly when someone is looking at it. The page is explicitly for the failure modes *above*
total database loss — a lane backing up, a job dead-lettering, a spike in failed sign-ins, latency
drifting — and `/health/ready` on both hosts, plus the OTLP path when it is enabled, remain
independent of it. Accepted knowingly, and the honest summary is that this page is an operations
tool, not an incident-response tool of last resort.

**Postgres acquires a fifth class of writer.** It already serves the application's own data, the
outbox pump, Wolverine's durable transport, and Quartz's scheduler tables. This adds operational
telemetry, with write volume proportional to *request* volume rather than to business events —
which is a different scaling curve from everything above it. It is bounded by construction: one
upsert per (bucket, name, pod) per minute, one row per job run, at most one `LastSeenAt` write per
user per minute. Bounded is not free, and it must be measured rather than assumed. If it ever
measurably matters, the exit is the one this ADR declined: move traffic aggregation to a real
metrics store and leave the domain-shaped tables here.

**`LastSeenAt` puts a write on the hottest path in the application.** ADR 0011's
`OnValidatePrincipal` read runs on every authenticated request; this adds a conditional `UPDATE`
alongside it. It must be a single statement with the throttle in its `WHERE` clause, never
read-modify-write, and it must never fail the request when it fails. Getting this wrong turns
every page load into a write.

**Retention becomes a correctness concern, not housekeeping.** An unpruned `traffic_buckets` grows
with traffic forever. The pruning jobs are part of the phases that create the tables, not a
follow-up, and a monitoring page whose own tables exhaust the disk would be a memorable way to
cause the incident it exists to detect.

**Some duplication with OpenSearch, where both are running.** A failed job appears as a
`job_runs` row and as log records. That is accepted: they answer different questions, and the
`TraceId` joins them rather than forcing a choice.

**This rules out arbitrary log search in the page**, permanently. There is no "grep the last hour
for this string" — that is what OpenSearch Dashboards is for, and the page links to it.

**It rules out long-horizon analysis.** Thirty days of events and seven of traffic answer "what is
happening" and "what happened last week". They do not answer "how has p95 moved this quarter". A
longer horizon means a rollup of the rollups, or a real metrics store.

**And it rules out sub-minute precision.** Buckets are truncated to the minute on each pod's own
clock, and clock skew between pods is real. Nothing may be built on this that needs finer
resolution than a minute.

**The page works everywhere the application works**, which was the point. Dev, e2e, CI, kind with
and without `-WithObservability` — all identical, with no component to start first and nothing to
configure.

## Alternatives considered

**Query OpenSearch from the API.** The apparent obvious answer, since the logs are already there.
Rejected on three counts. It is absent in every environment but one, so the page would be empty in
dev, in e2e and in CI — including in every test that might have verified it. It promotes the
heaviest optional component in the cluster to a hard dependency of a core page. And most
fundamentally, OpenSearch holds log *records*, not job *runs*: reconstructing "this job ran, was
retried twice, and dead-lettered" means parsing and correlating log lines, which is exactly the
burden ADR 0015 says the logging behavior should not be asked to carry, and which would break the
first time a message was reworded.

**Prometheus and Grafana.** The industry-standard answer for the traffic half, and genuinely
better at it than minute buckets in Postgres. Rejected because it answers only that half: it has
no path from "error rate spiked at 14:02" to "this job failed, here is its exception, here is the
order it touched, here is its trace." Drill-down into domain entities is most of what this page is
for. It is also a second data store and a new set of cluster components, adopted for one page,
against a decision to keep operational data in the store this application already runs.

**One row per request, aggregated in SQL.** Gives exact percentiles and complete drill-down with
no histogram arithmetic. Rejected on write volume: a write on the hot path for every request, and
a table whose growth is unbounded in the one dimension hardest to prune safely. The fixed
histogram buckets are the accepted trade — approximate percentiles, bounded writes.

**In-memory counters, per pod, with the page saying so.** Zero storage and zero write load.
Rejected because the ingress cookie affinity makes it actively misleading rather than merely
partial: an operator would be pinned to one pod and would see its numbers, stably, and would have
no reason to doubt them. A wrong number that looks right is worse than no number.

**Wolverine's envelope tables as run history.** They are already there, already durable, and
already record every message. Rejected because they are a durable transport's *working set*, not a
history: Wolverine owns and migrates that schema, and it prunes successfully handled envelopes —
so the one thing the table reliably would not contain is a record of everything that worked. Read
for dead letters, where they genuinely are the source of truth; never relied on for history.

**Quartz's execution history.** Rejected already, by ADR 0017, in terms this ADR simply inherits:
it records that a trigger fired, never what the job then did.
