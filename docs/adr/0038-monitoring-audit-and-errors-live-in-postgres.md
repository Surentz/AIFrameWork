# 0038. Monitoring's audit trail and error log are stored in Postgres, never read from the log store

**Date:** 2026-10-29
**Status:** Accepted

## Context

The monitoring section is to show two new things: **what users have done**, for auditing, and
**unhandled exceptions**, for debugging. The obvious source for both looked like the logs, so the
first question was whether logs are being saved to OpenSearch at all. The investigation
(2026-10-29) found:

- **Log export is off by default.** `Observability:Otlp:Enabled` is `false` in both hosts'
  `appsettings.json` (ADR 0015). A running kind cluster deployed without `-WithObservability` had no
  collector, no OpenSearch, and no OTLP keys in `app-config`, so its only logs were pod console output,
  lost on restart.
- **OpenSearch is opt-in and short-lived.** It exists only in the `local-observability` overlay
  (`deploy.ps1 -WithObservability`), and its ISM policy deletes `otel-logs-*` indexes after 7 days.
  The dev loop sends logs to Seq, and only with `dev.ps1 -WithSeq`. CI and the e2e stack send them
  nowhere.
- **The app never reads the log store.** The monitoring page reaches it only through outbound trace
  links (`Monitoring__TraceLinkTemplate`), which are plain text when no store is configured.
- **The question was settled once already, for the existing pages.** ADR 0021 put job runs,
  sign-ins and traffic in Postgres for these same reasons: they are *operational facts*, queried by
  known columns, while log lines are *diagnostic detail*, searched in a store that is usually absent.
  `sign_in_events` (30-day prune) and the administrator actions on the Users page are tables the API
  reads like any other.

Extends ADR 0021 to two more kinds of operational fact.

## Decision

- **The audit trail and the error log are Postgres tables**, written by the application and read by
  the monitoring API behind `Monitoring.Read`, exactly as the sign-in audit is.
- **Neither page queries OpenSearch, Seq, or any other log store.** The log store stays the place for
  full detail. Every audit and error row carries the bare 32-hex trace id, so the existing `TraceLink`
  takes an administrator from a row to its log lines whenever a store is configured.
- **The error log records only what the API answers with a 500.** A failed `Result` is a 4xx the
  caller can act on, a `DomainException` is a 400, and a request the client aborted is not an error:
  none of them are recorded. Making sure ordinary mistakes really are 4xx is the first piece of work,
  so that the log holds only real failures.
- **Each table's exact contents, retention and pruning are settled in its own design**, before it is
  built. Both tables hold personal data (a user id, and an exception message can quote user input),
  so both get a retention prune, as `sign_in_events` does.

## Consequences

Easier:

- Both pages work on every target: the dev loop with or without Seq, the plain kind overlay, CI and
  e2e. A Playwright spec can assert a row without a log store running.
- Retention is set by what auditing needs, not by the log store's 7-day ISM policy.
- They are queried the same way as every other monitoring view: EF Core, keyset paging and the same
  authorization, with no new outbound client or query language.

Costs:

- **A second copy of what is logged.** A 500 is both logged (with its full exception) and stored as a
  row, and the two can disagree in detail. The row is a summary; the log line is the truth.
- **Writes on the request path.** Every audited action and every 500 is an extra INSERT. A 500 caused
  by Postgres being down cannot be recorded either. Its log line still goes out, but the error page
  will not show that outage.
- **Two more tables that grow** and need a prune, and two more places personal data lives.
- **The error log covers the API only** unless its design extends it. The worker's failures already
  appear on the Jobs page as job runs and dead letters.
- **Logs stay invisible in-app.** Debug lines, warnings and anything else not recorded as a row are
  seen only in the log store, so `-WithObservability` or `-WithSeq` is still needed for full
  debugging.

## Alternatives considered

- **Read OpenSearch from the API.** Lost for the reason ADR 0021 gives, which still holds: OpenSearch
  exists only under `-WithObservability`.
  The pages would be empty in dev, on the default cluster, in CI and in e2e. Its 7-day retention is
  too short for an audit, and it would add a second datastore the API depends on to render a page.
- **Read whichever log store is configured (Seq or OpenSearch) behind an Application port.** Two
  query languages and two adapters, with the same retention and "empty when not deployed" problems.
  A port does not make a log store durable.
- **Keep linking out only.** No change to build, but it is exactly what was asked to go beyond: the
  administrator has to leave the app, and needs a log store to be running before seeing anything.
