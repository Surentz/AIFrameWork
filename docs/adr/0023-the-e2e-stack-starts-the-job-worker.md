# 0023. The e2e stack starts the job worker

**Date:** 2026-09-23
**Status:** Accepted

## Context

The Playwright suite's managed stack (ADR 0012) started Postgres, the API and the preview build.
Since ADR 0016 jobs run only in `src/Worker`, and the API listens to no queue. Without the worker,
a job the stack enqueued (a scheduled job run from `/monitoring/jobs`, or the
`SendOrderConfirmation` that every placed order enqueues) sat in Postgres and never ran. The
job-runs table had nothing to show, and nothing end to end covered the path from domain event to
outbox to queue to worker.

That path crosses two processes. Nothing below e2e runs both, so leaving the worker out left that
path with no test at all.

The outbox pump is registered in `AddInfrastructure` and so runs in the API as well. Notifications
therefore never depended on the worker, and that stays true.

## Decision

`playwright.config.ts` starts `src/Worker` as a third `webServer` on the managed (`local`) target.
It runs against the same e2e database, on `WORKER_PORT` (default 5235, from `e2e/support/env.ts`),
and its `/health` endpoint is the readiness check.

Both .NET hosts are built once, in `e2e/setup/prepare-database.ts`, before Playwright starts.
Both `webServer`s then start with `dotnet run --no-build`.

Specs that need a job to have run wait on the job-runs API (`api.countJobRuns`) rather than on the
page. They compare against a count taken before acting, because scheduled jobs may already have
runs from their cron or from earlier tests.

## Consequences

- Scheduled-job triggering and the order-confirmation pipeline are covered end to end
  (`monitoring/jobs.spec.ts`).
- A managed run starts one more process. On the authoring machine the full suite still finished
  in about 43 seconds.
- 5235 is also the dev loop's worker port. `npm run e2e` now collides with a running dev worker,
  not only the dev API, so stop the dev loop or set `WORKER_PORT`.
- `--no-build` means `npx playwright test` run *without* `e2e/setup/run.ts` (for example, directly
  from an IDE) starts whatever binaries were last built. `run.ts` always runs the build, which is
  one more reason it is the only entry point.
- The job specs are `@local-only`. An arbitrary URL target cannot be assumed to run a worker, and
  they need the administrator role anyway.
- The order-confirmation test proves the pipeline, not a particular order. A job run does not
  record which order it confirmed, and parallel specs place orders too.
- Nothing dead-letters a message on purpose, so the retry path on the dead-letter panel remains
  uncovered end to end.

## Alternatives considered

- **Leave the worker out and cover jobs below e2e only.** This lost because the one thing e2e
  could uniquely prove, one process enqueueing and another running, is exactly what it could not
  reach.
- **Let each `webServer` build its own host with a plain `dotnet run`.** This lost because
  Playwright starts `webServer`s in parallel. The API and the worker share `Domain`,
  `Application` and `Infrastructure`, so two concurrent builds race on the same `obj/` files. A
  compile error there also surfaces only as "Process from config.webServer was not able to start.
  Exit code: 1".
