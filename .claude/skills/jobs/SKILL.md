---
name: jobs
description: Use when editing or debugging a background job in this repo - lanes, why a job never runs or cannot see its user, Quartz scheduling, retry and dead-lettering, and the worker's config. Use /job to scaffold a new one.
---

# Jobs

**Jobs run in the worker. The API listens to nothing.** That is the whole rule, and it is
enforced rather than intended: `ApiPublishesOnlyTests` asserts against the runtime's own endpoint
list that no `aiframework.jobs.*` queue has a listener on the API host, and `JobDeliveryTests` asserts the
mirror image on the worker. The API registers *routing* for every lane — publishing is how a job
starts — and never `ListenToRabbitQueue`.

The reason is the API's own health: a job sharing the API process competes for the thread pool,
the GC heap under a 768Mi limit, and the Npgsql pool, and every API rollout would kill work in
flight. ADR 0016.

To **add** a job, use `/job <Name>` — it walks the full scaffold. This skill is for everything
after that.

## A job is a message with a lane

```csharp
public sealed record RebuildOrderReport(Guid OwnerId) : IUserScopedJob
{
    public static JobLane Lane => JobLane.Heavy;
}
```

| Lane | Queue | For | Parallelism per pod |
|---|---|---|---|
| `Light` | `aiframework.jobs.light` | Milliseconds to seconds, a round trip or two, negligible CPU | 8 |
| `Heavy` | `aiframework.jobs.heavy` | Seconds to minutes, CPU- or memory-bound, or fanning a large result set | 2 |

Both are RabbitMQ quorum queues (ADR 0026).

The lane picks the queue; `Jobs__Queues` picks which host listens. Both lanes run on one worker
today — splitting them onto differently-sized Deployments later is a manifest copy and no code
change.

Enqueue through `IJobScheduler` (`src/Application/Abstractions/Jobs.cs`); Wolverine never appears
in `Application`. Every job type must appear in `src/Infrastructure/Jobs/JobRegistration.cs` —
explicit and greppable, mirroring `AddMessaging()`, with `JobRegistrationTests` failing the build
on an omission.

## Diagnosing a job that misbehaves

| Symptom | Almost always |
|---|---|
| Enqueued, nothing happens, no error anywhere | The worker is not running. `dev.ps1` starts it; on its own, `./scripts/worker.ps1` |
| Job runs but reads nothing, or fails `Unauthorized` | It needs `IUserScopedJob` and an `OwnerId`. There is no `HttpContext` in the worker |
| Works in Debug, worker dies at startup in Release | Stale generated adapters. See the `regenerate` skill |
| Job ran, but the API still serves stale data | Expected. A job cannot evict the API's cache — the TTL bounds it |
| Command rolled back, job ran anyway | `EnqueueAsync` is not transactional. Raise a domain event instead |
| Schedule missing or wrong after a rollout | An old-build pod re-synced the store. Restart a worker |
| Worker refuses to start naming a `qrtz_` table, or `Database schema validation failed` naming Quartz's own `.sql` scripts | A missing migration. `dotnet ef database update` — do not run Quartz's scripts by hand and do not switch to `CreateIfMissing` |

## The six things that will cost you time

- **An enqueue is NOT transactional with the caller's work.** `EnqueueAsync` from a command
  handler publishes immediately, so a command whose transaction then fails still runs the job.
  **For a job that must not be lost, raise a domain event and enqueue from its handler** —
  `DomainEventsInterceptor` writes the outbox row in the same `SaveChangesAsync` as the
  aggregate, so the job exists if and only if the command committed.
  `OrderPlacedConfirmationHandler` is the reference. This is measured, not preference:
  Wolverine's own EF Core outbox *enrolls the DbContext*, opening a transaction that
  `UnitOfWork`'s plain `SaveChangesAsync` then cannot commit. `JobEnqueueMechanismTests` pins it.
- **There are TWO generated-code trees.** Debug stays green with either one stale; only Release
  breaks, at startup. CI checks both.
- **A job cannot evict the API's cache.** `HybridCache` is L1-only, and the worker is not behind
  the ingress cookie affinity that makes eviction work between API pods at all (ADR 0010). The
  worker therefore runs with `Cache__Enabled=false` so this is explicit rather than subtle.
- **There is no `HttpContext` in the worker.** A job touching user-owned data implements
  `IUserScopedJob` and carries the owner; `JobUserMiddleware` populates `ICurrentUser` from it
  before the handler runs. Forgetting is not a leak — ADR 0007 puts ownership in the query, so
  the job reads nothing — but it is a job that silently never works.
- **`ICurrentUser` is registered per host, never in `AddInfrastructure`.** The API binds it to
  the cookie's claims, the worker to the job. Binding it inside `AddJobs` replaces the API's,
  because the last registration wins, and every authenticated request then reports no caller.
- **Quartz 4 is not Quartz 3.** See below.

## Writing a handler

- **Throw on failure.** Wolverine's error policy decides retry versus dead-letter and can only
  see an exception. A handler that returns quietly on failure looks identical to one that
  succeeded, in every log and every metric.
- **Do not log the outcome.** `Behaviors.LoggedAsync` already records outcome and duration for
  anything dispatched, and Wolverine logs the message lifecycle. No "starting X", no "finished X".
- **Go through `ICommandDispatcher`/`IQueryDispatcher`** for domain state rather than reaching
  into a repository — that keeps validation and the logging behavior on the path.
- **Carry what the handler needs on the message** where that is cheap. A light job that opens a
  database connection to look up what it was already told is not a light job.
- Delivery is **at-least-once**. Every handler will run twice eventually; make it idempotent.
  `DomainEventContext.MessageId` is the dedupe key on the outbox path.

## Retry and dead-lettering are policy, never hand-written

```csharp
opts.OnAnyException()
    .ScheduleRetry(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30))
    .Then.MoveToErrorQueue();
```

**`ScheduleRetry`, not `RetryWithCooldown`.** A cooldown holds the message — and therefore a
listener slot — in memory for the whole delay. On the heavy lane, which runs at a parallelism of
2, one poisoned message sitting through a 30-minute cooldown would consume half the pod's
capacity. `ScheduleRetry` puts the envelope back into durable storage and frees the slot
immediately.

Message success logs at `Debug`, matching the "success is Debug" convention — Wolverine's default
of `Information` would make a busy light lane pure "it worked" noise.

## Scheduled jobs use Quartz as the clock

```csharp
JobDescriptor.Scheduled<PruneProcessedOutbox>("0 5 * * * ?")   // Quartz cron, SECONDS FIRST
```

It fires on exactly one worker, which enqueues it; Wolverine then runs it like any other job.
**Only parameterless jobs can be scheduled, and the compiler enforces it** — a schedule fires with
no caller and no arguments, so a job needing an owner is enqueued, never scheduled.

- **`EnqueueScheduledJob` is the only Quartz job there should ever be.** A second one would be a
  second way to write a job, with no lane, retry policy or dead-letter queue.
- **Never start a scheduler in the API.** `ApiHasNoSchedulerTests` keeps it that way.
- **Quartz's tables live in the `quartz` schema, created by an EF migration.** A Quartz upgrade
  that changes its schema is a new migration.
- **Every node needs its own instance id.** With clustering on and nothing configured, Quartz 4
  (still so in 4.2.1) names every node `NON_CLUSTERED` and two pods look like one.
  `ProcessInstanceIdGenerator` supplies one, because the built-in generators are `internal`.
- **The last build to start wins.** An old-build pod restarting mid-rollout re-syncs the store to
  *its* schedules. If a schedule is wrong after a rollout, restart a worker.

### Quartz 4 is not Quartz 3, and most samples online are 3.x

Checked against the 4.1.0 package:

| Quartz 3 | Quartz 4 |
|---|---|
| `IJob.Execute(IJobExecutionContext)` returns `Task` | returns `ValueTask`, with `CancellationToken = default` |
| `GetJobKeys` | `QueryJobs(new JobQuery { Group = ... })`, paging via `.Items`/`.HasMore` |
| `CheckExists` | `Exists` |
| `CronExpression.IsValidExpression` | `TryParse` |
| settable `SchedulerName` | not settable |

Misfires are set with `WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed)`.

## Configuration

| Key | Why it matters |
|---|---|
| `Jobs__Queues` | The lanes this pod consumes, comma-separated. **This one value is the host split** — a host listing no lane is publish-only, which is what the API does. An unknown name fails at startup |
| `Jobs__LightParallelism` / `Jobs__HeavyParallelism` | Per-pod concurrency per lane. Heavy defaults to 2 deliberately; scale replicas, not this |
| `Jobs__Schedules__<JobName>` | Overrides one scheduled job's cron. An unknown job or invalid cron fails startup naming the key. Only the *timing* is configurable; which jobs are scheduled is code |
| `Cache__Enabled` | `false` on the worker, deliberately — see above |
| `Observability__ServiceName` | `aiframework-worker`, so the two hosts are distinguishable in the log store |

**Double underscores, always.** A single underscore binds nothing and warns nothing.

## Testing

| Project | Covers |
|---|---|
| `tests/Application.Tests` | The handler, ports substituted with NSubstitute. No Wolverine |
| `tests/Infrastructure.Tests/Jobs` | Registration completeness, lane-to-queue mapping |
| `tests/Worker.IntegrationTests` | The job reaching its handler on the right lane |

Two rules for the worker suite, both learned from failing tests rather than reasoned out:

- **`IncludeExternalTransports()` is required on a tracking session.** A job goes out to a
  RabbitMQ queue and comes back through the host's own listener, and a session ignores external
  transports by default — without it the session sees the message "Sent", stops waiting, and
  every delivery assertion fails with "No messages of type … were received".
- **Never use a tracking session to prove something did *not* happen.** `ExecuteAndWaitAsync`
  waits for a message to be handled, so a message that must never be handled only ever produces a
  timeout. Assert on the stored envelope instead: a scheduled job waits in
  `wolverine.wolverine_incoming_envelopes` with status `Scheduled` until due; RabbitMQ has no
  delayed delivery.

In a Quartz test, fire a trigger with `IScheduler.TriggerJob(...)` and observe through a tracking
session — never wait for a cron to come round.

## Jobs ride RabbitMQ

Jobs ride RabbitMQ (ADR 0026); Postgres still holds the envelope storage. The `messaging` skill
has the broker topology, outage behaviour, and the check to run before upgrading a database that
still has jobs in the old Postgres queues.
