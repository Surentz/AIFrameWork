# 0029. Order exports are stored in Postgres and fail by staleness

**Date:** 2026-10-04
**Status:** Accepted

## Context

A signed-in user can now ask for a CSV of every order they have placed. Building it pages through an
unbounded history, which must not run on a request thread, so it runs as a job in the worker (ADR
0016) and the user is told when it is ready through the notification feed, pushed over SignalR.
That is the "the worker finishes, the user is told" path whose push ADR 0028 fixed, and this is
its first real user. It also replaces `RebuildOrderReport`, the placeholder heavy job that paged a
user's orders, logged a count, and was never enqueued.

Three questions needed answers that the rest of the codebase does not already settle: where the
finished file lives, what happens when its build job fails for good, and how long a copy of
someone's order history is kept.

## Decision

**The file is stored in Postgres.** An `order_exports` row holds the export's status and, once it
is built, the CSV as a `text` column. Listing exports projects that column away; only the download
reads it.

**Everything goes through the outbox.** `RequestOrderExport` commits the row and an
`OrderExportRequested` outbox row together; `OrderExportJobEnqueuer`, a domain event handler,
enqueues `BuildOrderExport` on the heavy lane. The job pages the owner's orders through
`GetOrderExportRows`, builds the CSV, and completes the export through `CompleteOrderExport`, whose
`OrderExportCompleted` event an API replica turns into an `OrderExportReady` notification. A
request therefore cannot commit without its job, and an export cannot finish without its
notification.

**A redelivered build completes the export at most once.** Completing a Ready export is a no-op,
and the job stops before doing any work once the export is Ready — which covers a second delivery
that runs after the first committed. Two deliveries that overlap both read Requested, so the row
carries Postgres's `xmin` as a concurrency token, as `orders` does: the loser's save fails, the job
throws and is retried, and the retry finds the export Ready. Without the token both would save,
and the owner would be told twice.

**Failure is read, not stored.** There is no Failed status. A failing build job throws, the worker
retries it after 1, 5 and 30 minutes and then dead-letters it; nothing could record that without a
`catch (Exception)`, which this repository does not allow. Instead an export still `Requested`
after `OrderExport.StaleAfter` — **45 minutes** — reads as Failed. It then no longer counts as in
progress, so asking again starts a fresh build. The figure is derived from the retry schedule
(36 minutes of retry delays, plus time for the builds themselves), and
`OrderExportStalenessTests` fails if `JobRegistration.JobRetryDelays` outgrows it.

**Exports are kept for seven days** (`OrderExports__RetentionDays`). `PruneOrderExports` runs daily
at 03:40 and deletes rows by request time, so failed requests go too.

## Consequences

- No new infrastructure. The file is already in the dev loop, the e2e stack, Testcontainers and
  the cluster, it is backed up with everything else, and downloading it is an ordinary
  owner-scoped query (ADR 0007): another user's export id answers 404, the same as one not built.
- **Large exports cost table space and memory.** The whole CSV is built in the worker's memory and
  stored in one row. There is no size cap. At this application's scale that is fine; a user with
  hundreds of thousands of orders would need streaming and object storage, which is the change to
  make if that ever happens.
- **A failed export says so only after 45 minutes.** Until then the screen says it is being built,
  because a retry may still succeed. If a late retry does succeed, the export becomes Ready and the
  user is notified anyway: the status only moves forward. The dead letter is visible on the
  monitoring page throughout.
- **A copy of someone's order history exists for up to seven days.** That is a decision, not a
  default: lengthening it keeps more personal data at rest.
- **Two simultaneous requests can start two exports.** A request returns the one in progress, but
  the check and the insert are not atomic, so two that arrive within milliseconds (two tabs, two
  replicas) each start one. Accepted: the page's button is inert while a request is pending, and
  the worst case is a second file and notification. A per-user lock or a partial unique index
  would close it, at the cost of clearing stale requests out of that index.
- A retention below one day fails the worker at startup (`OrderExportOptionsValidator`): zero
  would delete every export, the ones being built included.
- A notification can outlive its export. Its link then opens the exports page, where the export is
  gone, and the download answers 404. Feed rows are not deleted with the export.
- The CSV defuses spreadsheet formulas in the three user-typed columns (sku, product name,
  cancellation reason) with a leading apostrophe, and is served with a UTF-8 byte-order mark so
  spreadsheets read non-ASCII names correctly.

## Alternatives considered

**Object storage (MinIO or S3) for the file.** The production-grade answer for large files, but a
new container in compose, the e2e stack and the cluster, credentials, and a Testcontainers fixture
of its own — a project larger than the feature, for sizes this application does not have.

**Building the CSV on download, with no job.** The simplest design, but it puts unbounded work
back on a request thread and drops the reason for the feature: the worker, and being told when it
is done.

**Enqueuing the job directly from the request.** One hop shorter, but `EnqueueAsync` is not
transactional with the command: a failed commit would still build an export that does not exist,
and a crash between the two would lose the job.

**Having the job write the notification itself.** The worker cannot push (ADR 0028), so the user
would see "ready" only on their next poll, up to thirty seconds later.

**Storing Failed, by catching the build job's failure.** It needs `catch (Exception)` in the job or
a dead-letter hook that writes back to the export, either of which is a second failure path beside
Wolverine's retry policy. Reading staleness needs neither.

**A shorter cut-off (15 minutes, as first proposed).** Shorter than the retry schedule, so the
screen would say Failed while a retry was still coming, and "Try again" would start a second build
beside it.
