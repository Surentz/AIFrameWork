# Scheduled jobs: Quartz.NET as the clock, Wolverine as the runner

**Date:** 2026-09-16
**Status:** Implemented (branch `claude/quartz-scheduling`, 2026-09-19)
**Piece 1 of 5** in the job-monitoring roadmap (below).

## Context

ADR 0016 built a job framework with no scheduler: recurring work was to be a *self-rescheduling
durable message*, and Quartz was rejected as "a second scheduler authority, to buy a cron
expression". Two things have changed since.

**The recurring pattern turned out to have a hole.** The first real recurring job considered —
moving the outbox retention sweep off the API — exposed that the spec's "seeded idempotently at
worker startup" was never designed: with two worker replicas, each seeds its own chain and the job
runs twice as often. The pattern also has no misfire handling (a run missed while no worker was up
is simply lost), no way to list or pause a schedule, and a chain that dies silently the first time
a run dead-letters.

**The roadmap now expects many scheduled jobs and a monitoring page** that runs jobs, shows their
runs and logs, and shows the health of external integrations. That is exactly the adoption trigger
the ADR 0016 follow-up research named ("more than about three recurring jobs, or operators need to
see or pause schedules").

This spec covers only the scheduler. The roadmap:

| # | Piece | Depends on |
|---|---|---|
| **1** | **Quartz scheduling in the worker** (this spec) | — |
| 2 | Admin authorization — the app has no roles or policies at all today | — |
| 3 | Job run history and logs | 1 |
| 4 | Integration health checks, on an endpoint separate from readiness | — |
| 5 | Monitoring page in the React UI | 2, 3, 4 |

Each piece is its own spec → plan → build cycle.

### What was verified, rather than assumed

Against **Quartz 4.1.0** (Apache-2.0, released 2026-09-13), restored into the NuGet cache and
compiled against in a probe project:

| Fact | How it was checked |
|---|---|
| 4.x targets `net10.0` only; the package ships `lib/net10.0` | package contents |
| `services.AddQuartz(q => q.UsePersistentStore(s => { s.UsePostgres(cs); s.UseClustering(c => c.Enabled = true); }))` and `services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true)` | **compiled** |
| `IJob.Execute(IJobExecutionContext, CancellationToken)` — 4.x passes the token | XML docs |
| `AdoJobStoreOptions.SchemaProvisioning` = `None` / `Validate` / `CreateIfMissing`, **default `Validate`**; `ProvisionSchema()` is shorthand for `CreateIfMissing` | XML docs |
| The Postgres schema script is embedded as `Quartz.Impl.AdoJobStore.Schema.create_postgres.sql`: 12 tables plus indexes, every statement `IF NOT EXISTS`, nothing dropped | **extracted and read** |
| The script's `{0}` is the table prefix and may carry a schema qualifier; `{1}` is the prefix without it | the script's own header |
| `CronScheduleBuilder.WithMisfireInstruction` taking `CronTriggerMisfireInstruction.FireAndProceed` (the 3.x `WithMisfireHandlingInstruction…` methods are gone), `ClusteringOptions.{Enabled, CheckinInterval, CheckinMisfireThreshold}` | XML docs |
| `QuartzInstrumentation.ActivitySourceName` exists for OpenTelemetry | XML docs |
| `AddQuartzHealthChecks` exists on the Quartz builder | XML docs |
| Wolverine 6.33 has no cron or recurring scheduling of its own | installed package XML |

**Not yet verified, and Task 1 of the plan:** that `Validate` accepts a schema-qualified prefix
(`quartz.qrtz_`), that clustered start works against the vendored schema, and how Quartz 4
resolves an `IJob` from DI (scope per execution or not).

## Decisions

### Quartz is only the clock. Wolverine still runs every job

When a trigger fires, Quartz runs exactly one job type that we ever write — `EnqueueScheduledJob` —
and all it does is `IJobScheduler.EnqueueAsync(...)`. The job then runs through the ADR 0016 path
unchanged: its lane, its retry and dead-letter policy, `JobUserMiddleware`, the worker's generated
adapters.

```
worker ── Quartz (clustered, Postgres store, schema `quartz`)
            trigger "PruneProcessedOutbox" fires on exactly one worker
              └─ EnqueueScheduledJob
                   └─ IJobScheduler.EnqueueAsync(new PruneProcessedOutbox())
                        └─ Wolverine jobs_light → handler
```

There is one way to write a job and one way to run it, whether the trigger is a schedule, a domain
event or (in piece 5) a button. That is what lets piece 3 build run history once, at the Wolverine
layer, and see every run.

The cost is accepted: Quartz 4.1's own execution history would record only "fired", never the
job's outcome, so it is not what piece 3 will read.

### A schedule is declared on the job's registration

```csharp
public static IReadOnlyList<JobDescriptor> Jobs { get; } =
[
    JobDescriptor.For<SendOrderConfirmation>(),
    JobDescriptor.For<RebuildOrderReport>(),
    JobDescriptor.Scheduled<PruneProcessedOutbox>("0 5 * * * ?"),   // every hour at :05
];
```

`JobDescriptor.Scheduled<TJob>(cron)` is constrained `where TJob : IJob, new()`. **Only jobs with a
parameterless constructor can be scheduled, and the compiler enforces it**:
`JobDescriptor.Scheduled<RebuildOrderReport>(...)` does not build, because a job that acts for one
user has no meaning on a global timer. The descriptor captures `static () => new TJob()` and the
enqueue call at construction, so the scheduled path stays reflection-free like the rest of
`JobRegistration`.

The cron is a Quartz cron expression (seconds first) and is validated when the descriptor list is
built, so an invalid default fails the completeness test rather than a deployed worker.

### An environment can override a schedule; nothing else can edit one

`JobOptions.Schedules` is a dictionary keyed by job type name:

```yaml
# k8s ConfigMap
Jobs__Schedules__PruneProcessedOutbox: "0 5 */6 * * ?"
```

An override for an unknown job, or with an invalid cron, **fails worker startup** naming the key —
the same contract `Jobs:Queues` already has. The repo stays the source of truth; config changes
the timing per environment without a code change.

The monitoring page (piece 5) will be able to **pause, resume and run now**. It will not edit a
cron; that stays in review.

### Schedules are synced into Quartz at worker startup

A small `ScheduleSynchronizer` runs once when the worker's scheduler starts:

- **Creates or updates** each scheduled job's trigger by a fixed key (`jobs.<JobTypeName>`), with
  the effective cron — override if present, else the default.
- **Never changes a trigger's paused state.** A pause from the monitoring page survives redeploys
  and restarts. Updating a cron on a paused trigger leaves it paused.
- **Deletes** triggers and jobs in our group whose job type is no longer scheduled, so removing
  `.Scheduled(...)` from code actually stops the job.
- Only touches the `jobs` group, so nothing else in the store is ever at risk.

With several worker replicas starting at once, the store's cluster locks serialise the writes, and
every replica writes the same values.

### Misfires: one catch-up run, then carry on

`CronTriggerMisfireInstruction.FireAndProceed` (set through `CronScheduleBuilder.WithMisfireInstruction`) is the default for every schedule: a worker that was
down through six runs produces **one** run when it comes back, not six. Jobs whose missed runs must
all execute are not expected; if one appears, it gets its own option then.

### Quartz runs clustered, in the worker only

- **Persistent store, Postgres, clustered.** This is forced by the other decisions rather than
  chosen alone: a pause must survive restarts and apply to every worker replica, and a trigger must
  fire on exactly one of them. An in-memory store can do neither.
- **Tables in a `quartz` schema, prefix `quartz.qrtz_`,** beside the `wolverine` and
  `wolverine_queues` schemas and apart from EF's `public`.
- **`SchemaProvisioning.Validate`** (Quartz's default, kept explicitly): the worker refuses to start
  on a missing or stale schema and names what is missing, rather than creating it behind the
  migration's back.
- **The scheduler is started only in the worker**, via `AddQuartzHostedService` with
  `WaitForJobsToComplete = true`. The API registers no Quartz at all in this piece. Piece 5 will
  give the API a scheduler that is configured but never started, for pause and resume only.
- **Quartz's job concurrency is irrelevant here**, because the only job it runs enqueues and
  returns in milliseconds; real concurrency control stays in Wolverine's lanes.

### An EF migration creates Quartz's schema

A new migration `AddQuartzSchema` runs `CREATE SCHEMA IF NOT EXISTS quartz;` followed by the
Postgres script **copied into the migration** with `{0}` → `quartz.qrtz_` and `{1}` → `qrtz_`.

- **Copied, not read from the embedded resource at run time.** An applied migration must never
  change behaviour (the protect-migrations rule). Reading the resource would make a Quartz upgrade
  silently alter what this migration does on a fresh database.
- **A Quartz upgrade that changes the schema becomes a new EF migration**, taken from Quartz's own
  upgrade script for that version. `Validate` at worker startup is what catches forgetting to.
- `dotnet ef database update` locally, and the existing `migrate` Job in Kubernetes, both pick it
  up with no new step. EF stays the one schema authority for everything except Wolverine's own
  tables (ADR 0005).
- `Down` drops the `quartz` schema, which EF migrations already allow for the tables they own.

### The first scheduled job: `PruneProcessedOutbox`

The outbox retention sweep moves out of `OutboxPollerService`, where it currently runs in **three
pods** (both API replicas and the worker) and issues the same `DELETE` every five minutes.

- `src/Application/Maintenance/PruneProcessedOutbox.cs` — the job (Light lane, parameterless), its
  handler, and a port `IOutboxRetention` whose one method returns the number of rows removed.
- `OutboxRetention` in Infrastructure implements the port by delegating to the existing `OutboxPoller.PruneAsync`,
  which is unchanged and keeps its own Postgres test where it is.
- `OutboxPollerService` loses `DueForPrune`, `_nextPruneDueAt` and the prune call;
  `OutboxOptions.PruneInterval` is removed. `RetentionPeriod` stays — it is still what defines
  "old enough to delete".
- Scheduled at `0 5 * * * ?` (hourly, at :05). The retention period is seven days, so hourly is
  already generous; the old five-minute interval existed only because the sweep was piggy-backing
  on the poll loop.

This moves real, repeated database work off the API — a first step of the "move the outbox pumps
off the API" follow-on — and exercises every part of this spec with a real job.

### Observability

`WorkerObservability` adds `.AddSource(QuartzInstrumentation.ActivitySourceName)`, so a trigger
firing and the Wolverine job it enqueues appear in one trace. The worker's `/health/ready` gains
Quartz's health check, so a worker whose scheduler failed to start is taken out of rotation rather
than looking healthy while nothing fires.

## Configuration

`src/Worker/appsettings.json` gains nothing mandatory — schedules default from code. An override:

```json
"Jobs": {
  "Schedules": { "PruneProcessedOutbox": "0 5 */6 * * ?" }
}
```

Quartz clustering uses its defaults (check-in every 7.5s); they are not exposed as configuration
until something needs them to be.

## Testing

| Project | Covers |
|---|---|
| `Infrastructure.Tests` | `JobDescriptor.Scheduled` — cron validation, key naming; `JobOptions.Schedules` — unknown job and invalid cron both fail validation; `JobRegistrationTests` — every scheduled descriptor has a valid cron; the existing `OutboxPollerTests` prune test, unchanged, still covers the SQL |
| `Application.Tests` | `PruneProcessedOutboxHandler` calls the port |
| `Worker.IntegrationTests` | The worker **starts with `Validate`** against the migrated schema (this is the test that proves the vendored SQL matches Quartz 4.1); each scheduled job has a trigger with the effective cron; an override changes it; **a paused trigger stays paused across a re-sync**; a descriptor removed from the list has its trigger deleted; **firing a trigger enqueues the job and Wolverine runs it** |
| `Api.IntegrationTests` | The API registers no Quartz scheduler; `ApiPublishesOnlyTests` unchanged |

No test waits for a cron to come round. Firing is driven with `IScheduler.TriggerJob(...)` and
observed through the Wolverine tracking session the worker suite already uses
(`IncludeExternalTransports()`).

Every integration fixture already runs `MigrateAsync`, so the `quartz` schema exists in all of them
without further setup.

## Consequences

**What this makes easy.** Adding a scheduled job is one `.Scheduled(cron)` on its registration.
Schedules fire once across any number of worker replicas, survive restarts, catch up once after
downtime, and can be paused. Piece 5's pause/resume buttons have something real to act on.

**What this costs.**

- **Twelve more tables** in a third schema, and Quartz's own row locking on every fire. At one
  hourly job this is negligible; it is the price of the roadmap, not of this job.
- **A vendored copy of Quartz's SQL.** Upgrades that change the schema need a new migration by
  hand, taken from Quartz's upgrade script. `Validate` makes forgetting loud rather than silent.
- **Quartz 4.x is weeks old.** The mitigation is Task 1: every API this spec relies on is exercised
  against the installed package and a real Postgres before anything else is built.
- **ADR 0016's recurring-job guidance is superseded.** Self-rescheduling messages are no longer the
  way to write a recurring job. ADR 0017 records this, and `CLAUDE.md`, `src/Worker/CLAUDE.md` and
  `/job` are updated to match.

**What this rules out.** Crons edited at run time, and Quartz executing job logic directly.

## Out of scope

- Admin authorization, run history, integration health checks and the monitoring page (pieces 2–5).
- The API's non-started scheduler for pause/resume (piece 5).
- Quartz's HTTP API and dashboard packages.
- Moving the rest of the outbox pumps off the API.

## Files

**Created:**
- `src/Application/Maintenance/PruneProcessedOutbox.cs`
- `src/Infrastructure/Jobs/Scheduling/QuartzRegistration.cs`, `EnqueueScheduledJob.cs`,
  `ScheduleSynchronizer.cs`
- `src/Infrastructure/Outbox/OutboxRetention.cs`
- `src/Infrastructure/Persistence/Migrations/<timestamp>_AddQuartzSchema.cs` (+ Designer)
- `tests/Infrastructure.Tests/Jobs/ScheduledJobTests.cs`
- `tests/Api.IntegrationTests/Jobs/ApiHasNoSchedulerTests.cs`
- `tests/Application.Tests/Maintenance/PruneProcessedOutboxHandlerTests.cs`
- `tests/Worker.IntegrationTests/Jobs/SchedulingTests.cs`
- `docs/adr/0017-quartz-as-the-job-clock.md`

**Modified:**
- `src/Infrastructure/AiFramework.Infrastructure.csproj` — `Quartz` 4.1.0
- `src/Infrastructure/Jobs/JobRegistration.cs`, `JobOptions.cs`
- `src/Infrastructure/Outbox/OutboxHostedServices.cs`, `OutboxOptions.cs`, `OutboxPoller.cs`
- `src/Infrastructure/InfrastructureRegistration.cs`
- `src/Worker/Program.cs`, `src/Worker/Observability/WorkerObservability.cs`
- `src/Worker/Internal/Generated/` — regenerated
- `tests/Infrastructure.Tests/Outbox/OutboxRegistrationTests.cs`
- `CLAUDE.md`, `src/Worker/CLAUDE.md`, `.claude/commands/job.md`, ADR 0016 (pointer to 0017)
