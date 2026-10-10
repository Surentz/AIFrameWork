# 0032. External system status is published by the worker

**Date:** 2026-10-09
**Status:** Accepted

## Context

ADR 0031 gave every external system a probe, a certificate and a token health check, tagged
`external` and kept out of `/health/ready`. Nothing recorded their results, so an operator could
not see a partner's state without a pod's logs. The monitoring page needs a stored status, and the
checks must not run when a person opens the page: that would let a browser tab probe a partner.

The design spec (section 3) chose a Quartz job every minute. `job_runs` records every run for 30
days, so a minutely job adds about 1,440 rows a day (about 43,000 retained) and buries the real
jobs on the Jobs page.

## Decision

**ASP.NET Core's `IHealthCheckPublisher` runs the checks, in the worker only.**
`AddExternalSystemStatusPublisher` registers `ExternalSystemStatusPublisher` there; the API never
does. The framework owns the timing: period `Monitoring:ExternalSystemStatusPeriod` (default one
minute), a 5 second initial delay, a 30 second timeout, and a predicate that selects only the
`external` checks. Both hosts' `/health/ready` keep excluding them (ADR 0031).

**Each run rewrites `external_system_status`.** One row per system: name, status (the worst of its
checks), description (from the worst check), `checked_at`, `certificate_not_after` and `token_ok`.
It is a last-write-wins upsert, plus a delete of every system no longer configured. Times are
stored as UTC. The worker owns the table's contents; the API only reads it.

**A row older than 3 minutes reads as stale** (the worker is not running), never as its last
status. This is failure by staleness, as ADR 0029 does for exports. Opening the page never probes
a partner.

**Descriptions are sanitised.** They reach the browser, so a check that threw is described as
`check failed: <ExceptionTypeName>`, never by its message, which can carry a host, a port or a
path.

**Metrics.** The meter `AiFramework.ExternalSystems` is added in both hosts and has two
instruments:

- the counter `aiframework.external_system.calls` (unit `{call}`, tags `system` and `outcome` of
  `succeeded`, `failed` or `faulted`), incremented by the `Outbound` counting handler outside every
  retry, so it counts logical calls and not attempts;
- the observable gauge `aiframework.external_system.certificate.time_remaining` (unit `s`, tag
  `system`), set by the worker's publisher.

Prometheus sees them as `aiframework_external_system_calls_total` and
`aiframework_external_system_certificate_time_remaining_seconds`.

**Two alerts**, with promtool tests: `ExternalSystemFailing` (more than 20% of a system's calls
`faulted` over 5 minutes, with at least 10 calls in the window, so one failed call at night does
not page anyone) and `ExternalSystemCertificateExpiring` (under 14 days remaining).

**The endpoint** is `GET /api/monitoring/external-systems` under `Monitoring.Read` (ADR 0020). It
joins each stored status with that system's outbound traffic. It is not `ICacheable` (ADR 0021).
The page is `/monitoring/integrations`, with a strip on `/monitoring`; both refresh every 30
seconds.

## Consequences

- **Two worker replicas both publish.** Each writes the same rows and the last write wins, so it is
  harmless, but it doubles the probes. A Quartz job would have fired on exactly one.
- **The API's own view of a partner is not measured separately.** The API and the worker share a
  cluster network and a certificate source, so the worker's result stands for both. A partner that
  rejects only the API's pods would not show.
- **Per-pod gauges are `min`-aggregated.** Each worker pod reports its own certificate gauge, and
  the alert takes the minimum across pods.
- **The status can be a minute old, plus the check timeout.** Hence 3 minutes before it is called
  stale.
- **The framework's `DefaultHealthCheckService` logs every Unhealthy check at Error.** With a
  partner down, that would be an error every minute per check, for something that is not the
  worker's fault. The worker's `appsettings.json` sets that category to `Critical`. The side effect
  is that the worker's own `/health/ready` failures are no longer logged; the 503 still drives
  Kubernetes. `ProbeHealthCheck` logs an unreachable host at Debug for the same reason.
- **The token check's timeout is 10 seconds**, so an IdP that hangs cannot hold the publisher past
  its 30 second budget.
- **A development machine reuses its end-to-end certificates.** Each fresh PKI leaks one
  same-subject intermediate into Windows `CurrentUser\CA` (ADR 0031), so the e2e run keeps
  `frontend/e2e/.certs/` and regenerates it only when a file is missing or `client.pfx` is over 365
  days old. CI generates it every run. Nothing is deleted from the certificate store.
- **The kind overlay does not mount partner certificates.** Nothing in the cluster calls a partner
  yet, and the VSO contract in ADR 0031 stands.

## Alternatives considered

**A Quartz job.** It was the spec's choice and is rejected for the `job_runs` reason above. Jobs
are for work whose run history matters; this is a heartbeat.

**A `BackgroundService` of our own.** Rejected: the publisher already owns the timing, the
timeout, the delay and the predicate, and a hand-written loop would have to rebuild all four.

**The API publishing.** Rejected: every API replica would probe every partner, and ADR 0016 already
puts timed work in the worker.

**Probing when the page is opened.** Rejected: it lets any administrator's browser tab spend a
partner's rate limit and a token request, and a slow partner would hang the page.
