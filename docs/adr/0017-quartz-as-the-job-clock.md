# 0017. Quartz.NET as the job clock

**Date:** 2026-09-16
**Status:** Accepted

## Context

ADR 0016 built a job framework with no scheduler: recurring work was to be a *self-rescheduling
durable message*, and Quartz was rejected outright — "eleven tables, a clustering story, and a
schema authority, to buy a cron expression that a self-rescheduling durable message provides with
no lock and no new tables."

The first real recurring job broke that plan. Moving the outbox retention sweep off the API — it
was running the same `DELETE` every five minutes in three pods (both API replicas and the worker)
— exposed that "seeded idempotently at worker startup" was never actually designed: with two
worker replicas, each seeds its own chain and the job runs twice as often. The pattern also has
no misfire handling (a run missed while no worker was up is simply lost), no way to list or pause
a schedule, and a chain that dies silently the first time a run dead-letters.

The roadmap now expects many scheduled jobs and a monitoring page that runs jobs, shows their
runs and logs, and shows the health of external integrations. That is exactly the adoption
trigger ADR 0016's own follow-up research named: "more than about three recurring jobs, or
operators need to see or pause schedules." This is piece 1 of that roadmap; admin authorization,
run history, integration health checks, and the monitoring page itself are separate pieces built
later, on top of this one.

## Decision

**Quartz 4.1 is the clock. Wolverine still runs every job.**

When a trigger fires, Quartz runs exactly one job type this codebase ever writes —
`EnqueueScheduledJob` — and all it does is resolve `IJobScheduler` from its own DI scope and
enqueue the job. The job then runs through the ADR 0016 path unchanged: its lane, its retry and
dead-letter policy, `JobUserMiddleware`, the worker's generated adapters. There is one way to
write a job and one way to run it, whether the trigger is a schedule, a domain event, or (in a
later piece) a button — which is what lets run history be built once, at the Wolverine layer, and
see every run. The cost is accepted: Quartz's own execution history would record only "fired,"
never the job's outcome, so it is not what run history will read.

**A schedule is declared on the job's registration:**

```csharp
JobDescriptor.Scheduled<PruneProcessedOutbox>("0 5 * * * ?"),   // every hour at :05
```

`JobDescriptor.Scheduled<TJob>(cron)` is constrained `where TJob : IJob, new()`. Only a job with a
parameterless constructor can be scheduled, and the compiler enforces it — a job that acts for one
user has no meaning on a global timer. The descriptor captures `static () => new TJob()` and the
enqueue call at construction, so the scheduled path stays reflection-free like the rest of
`JobRegistration`. The cron is validated (Quartz's own `CronExpression.TryParse`) when the
descriptor list is built, so an invalid default fails a completeness test rather than a deployed
worker.

**An environment can override a schedule; nothing else can edit one.** `Jobs__Schedules__<JobName>`
overrides a job's cron. An override naming an unknown job, or an unparseable cron, fails worker
startup naming the key — the same contract `Jobs:Queues` already has. The repo stays the source of
truth; config changes only the timing per environment.

**A `ScheduleSynchronizer` syncs the code's schedules into Quartz once, at worker startup.** It
creates or updates each scheduled job's durable job and cron trigger by a fixed key
(`jobs.<JobTypeName>`), deletes any trigger in the `jobs` group whose job type is no longer
scheduled, and — the fix that came out of building this — **leaves a trigger completely untouched
when its cron and misfire instruction already match**, rather than unconditionally rescheduling
it on every startup. `RescheduleJob` always stores a fresh trigger with its next-fire-time set to
the next *future* occurrence, which would silently discard a misfire this worker was meant to
catch up on, and briefly reset an operator-paused trigger to `Normal` between the reschedule and
the later `PauseTrigger` call. The synchronizer's own remarks document the one residual gap
honestly: a paused trigger whose cron changes in the *same* deploy still has a brief unpause
window between `RescheduleJob` and `PauseTrigger`, because Quartz exposes no atomic
reschedule-and-pause. That is narrower than "every startup" and is judged acceptable — closing it
needs an API Quartz does not have.

**Misfires get one catch-up run, then carry on.** Every scheduled trigger sets
`CronTriggerMisfireInstruction.FireAndProceed`: a worker down through six runs produces one run
when it comes back, not six. A job whose every missed run must fire gets its own option if one
ever appears.

**Quartz runs clustered, on a Postgres persistent store, in the worker only.** Forced by the other
decisions, not chosen alone: a pause must survive restarts and apply to every replica, and a
trigger must fire on exactly one of them, neither of which an in-memory store can do. Tables live
in their own `quartz` schema, prefix `quartz.qrtz_`, beside `wolverine`/`wolverine_queues` and
apart from EF's `public`. `SchemaProvisioning.Validate` — Quartz's own default, kept explicit —
means the worker refuses to start against a missing or stale schema rather than silently creating
one behind the migration's back. The scheduler is started only in `src/Worker/Program.cs`, via
`AddQuartzHostedService(o => o.WaitForJobsToComplete = true)`; the API registers no Quartz at all.

**An EF migration creates and owns the schema.** `AddQuartzSchema` runs
`CREATE SCHEMA IF NOT EXISTS quartz;` followed by Quartz 4.1.0's own bundled
`create_postgres.sql`, copied — not read from the embedded resource at run time — into the
migration `.cs`, with `{0}` → `quartz.qrtz_` and `{1}` → `qrtz_`. Copied rather than loaded live,
because an applied migration must never change behaviour; loading the resource at run time would
let a future Quartz package bump silently change what an already-applied migration does. The
whole script runs as a single `migrationBuilder.Sql(...)` call — Npgsql's simple-query protocol
runs the semicolon-delimited batch in one round trip, no splitting on the script's own `--;;`
markers needed. It lives inline in the migration file because `protect-migrations.ps1` guards
`Migrations/*.cs` and nothing else; a separate `.sql` alongside it would not be protected the same
way. A future Quartz upgrade that changes its schema needs a new migration, hand-written from that
version's own upgrade script — `Validate` at startup is what makes forgetting loud instead of
silent.

**Observability follows the trigger through to the job.** The worker's tracing adds
`Quartz.Diagnostics.QuartzInstrumentation.ActivitySourceName`, so a trigger firing and the
Wolverine job it enqueues appear in one trace. `AddQuartzHealthChecks()` inside the `AddQuartz`
builder registers into the same ASP.NET Core health-check registry the worker's existing
`/health/ready` already serves, confirmed by observing the check appear with no other wiring — a
worker whose scheduler failed to start is taken out of rotation rather than looking healthy while
nothing fires.

**The first scheduled job is `PruneProcessedOutbox`.** It moves the retention sweep out of
`OutboxPollerService` — which loses `DueForPrune`, `_nextPruneDueAt`, and the prune call entirely;
`OutboxOptions.PruneInterval` is gone, `RetentionPeriod` stays — into an hourly job
(`0 5 * * * ?`) running once, on one worker, through the same `OutboxPoller.PruneAsync` the poller
used to call directly. The outbox pumps themselves (`OutboxPollerService`, `OutboxWorkerService`)
are unchanged and still run in both the API and the worker; only the retention sweep left the
loop.

## Consequences

**What this makes easy.** Adding a scheduled job is one `.Scheduled(cron)` on its registration.
Schedules fire exactly once across any number of worker replicas, survive restarts, catch up once
after downtime, and can be paused without losing that state across a redeploy. A later
pause/resume/run-now UI has something real to act on.

**What this costs.**

- **Twelve more tables, in a third schema, and Quartz's own row locking on every fire.** At one
  hourly job this is negligible; it is the price of the roadmap, not of this job.
- **A vendored copy of Quartz's SQL.** An upgrade that changes the schema needs a hand-written
  migration, taken from Quartz's own upgrade script. `Validate` makes forgetting loud rather than
  silent, but it does not make the migration itself automatic.
- **Quartz 4.x was weeks old when this was built** (4.1.0, released 2026-09-13). Task 1's spike —
  a real clustered scheduler against a real Postgres, before anything else was built — exercised
  the load-bearing claims (`Validate` accepting a schema-qualified prefix, a clustered scheduler
  actually starting and firing) rather than trusting the documentation.
- **With clustering enabled and no instance id configured, Quartz 4.1 falls back to the literal
  id `"NON_CLUSTERED"` for every node** — confirmed in Task 1's spike log. Quartz's own built-in
  generators (`SimpleInstanceIdGenerator`, `HostNameInstanceIdGenerator`) are `internal` to the
  `Quartz` assembly in 4.1.0 and cannot be named from this codebase (`CS0122`), so
  `QuartzRegistration` supplies a small `ProcessInstanceIdGenerator` — hostname, process id, and a
  tick count — through the public `IInstanceIdGenerator` extension point. A worker integration
  test asserts the running scheduler's instance id is non-empty and not the sentinel, and that two
  hosts started against the same store get different ids. Quartz 4.1 also documents a public
  `QuartzSchedulerOptions.GenerateInstanceId` switch that was not adopted here — the custom
  generator is what was actually proven against the shipped package; switching to that flag later
  is a possible simplification, not a correction.
- **ADR 0016's recurring-job guidance is superseded.** Self-rescheduling messages are no longer
  the way to write a recurring job — see the note added to that ADR. `CLAUDE.md`,
  `src/Worker/CLAUDE.md`, and `/job` are updated to match.
- **What was not proven end to end: a genuine wall-clock misfire catch-up.** There is no
  `Thread.Sleep`-free way to advance a persistent-store, clustered scheduler's own clock — a
  `FakeTimeProvider` registered as the scheduler's `TimeProvider` did not affect a trigger's
  computed `NextFireTimeUtc`, and backdating a trigger's `StartAt` did not either; both were tried
  and both left the trigger computing off the real wall clock. The guarantee is protected by
  construction (`FireAndProceed` is the fixed misfire instruction on every scheduled trigger) and
  by a unit test pinning `ScheduleSynchronizer`'s own unchanged-cron logic, not by an end-to-end
  test that observes an actual missed-and-caught-up run.

**What this rules out.** Crons edited at run time — an override changes a value in configuration,
never a running trigger directly — and Quartz executing job logic itself. Every job still runs
through Wolverine, with one lane, one retry policy, and one dead-letter queue, whether it was
triggered by a schedule, a domain event, or a button.

## Alternatives considered

**Self-rescheduling durable messages (ADR 0016's own approach).** Already in production for the
handful of jobs that existed. Rejected on the three weaknesses this piece exists to close: no
misfire handling (a run missed while nothing was up is lost, not caught up), no way to list or
pause a schedule without redeploying code, and — the one that actually forced the change — no
safe way to seed the chain from more than one replica without either double-firing or building
leader election by hand.

**Quartz executing jobs directly**, instead of only enqueueing them. Rejected because it would
mean two ways to write a job — one that runs under Wolverine's lanes, retry, and dead-lettering,
and one that runs under Quartz's own, weaker job-execution model with no lane concept at all. A
later monitoring page would then need to read run history from two systems instead of one.

**Quartz executing all jobs**, retiring Wolverine's job path entirely. Rejected for the same
reason ADR 0016 chose Wolverine originally: Quartz has no lane/queue concept, no
`ScheduleRetry`-style backoff policy, and no dead-letter queue — all three would have to be
rebuilt on top of it, duplicating work ADR 0016 already paid for and already uses for the
domain-event path.

**A separate SQL script step for the schema**, run outside EF migrations (a manual `psql` step, or
a step in the Kubernetes `migrate` Job distinct from `dotnet ef database update`). Rejected
because it would make EF no longer the single schema authority for everything except Wolverine's
own tables (ADR 0005) — a second, hand-run step is a second thing to forget, with no protection
from `protect-migrations.ps1` and no automatic pickup by `dotnet ef database update` or the
existing `migrate` Job.

**`SchemaProvisioning.CreateIfMissing`**, letting Quartz create its own schema at startup instead
of validating one an EF migration already created. Rejected because it splits schema ownership:
EF would own every other table, and Quartz would silently create and evolve its own the first
time a worker with a newer package version started — exactly the kind of surprise `Validate`
exists to turn into a startup failure instead.

**A Kubernetes `CronJob`** per scheduled job, running `dotnet run --project src/Worker -- <job>`
on a cron schedule outside the application entirely. Rejected because it has no parity with the
`docker compose` development loop — a developer running `dev.ps1` would have no working schedules
at all — and because pause/resume, misfire handling, and run history would all need to be built
against the Kubernetes API instead of against one scheduler the roadmap already needs for other
reasons.

**A Wolverine agent** as the scheduling authority instead of Quartz. Rejected because Wolverine
6.33 has no cron or recurring-schedule concept of its own to build an agent on top of — it would
mean writing distributed pause state and misfire handling by hand, which is exactly the two things
Quartz already provides, tested, over its own persistent store.
