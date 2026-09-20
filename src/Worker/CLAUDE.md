# Worker

The job host. A second composition root beside `src/Api`, running the same `AddInfrastructure`
against the same database — the only difference is which Wolverine queues it listens on.

See ADR 0016, and root `CLAUDE.md`'s `## Jobs` section for the rules that apply wherever a job is
written. This file is what is specific to *this project*.

## Belongs here

Composition only: `Program.cs`, `appsettings*.json`, the OpenTelemetry pipeline in
`Observability/`, and the committed generated code under `Internal/Generated`.

## Never appears here

- Any `AiFramework.Api` namespace. **Blocked by the dependency-rule hook** — `Worker` is a sibling
  host, not a layer above `Api`.
- Controllers, DTOs, `MapControllers`. This host maps exactly two endpoints and nothing else.
- Job messages and their handlers. Those are Application code, in their feature folder
  (`src/Application/Orders/SendOrderConfirmation.cs` is the reference). Nothing feature-shaped
  lives here.

## Why this is a web host

`Microsoft.NET.Sdk.Web`, not `Sdk.Worker`, and that is not laziness. Kubernetes cannot probe a
process that serves no HTTP: an `exec` probe needs a shell, and the chiseled runtime image has
none — the same fact `k8s/base/api.yaml` records when explaining why its `preStop` uses the native
sleep hook. So this host serves `/health` and `/health/ready`, and that is the entire HTTP surface.

Two things fall out of it that are worth having on purpose: a `WebApplicationBuilder` means
`AddWorkerObservability` needs no `IHostApplicationBuilder` gymnastics, and
`RunJasperFxCommands(args)` works exactly as it does in `src/Api`, which is what makes this
project's own `codegen write` reachable.

## The generated code is this project's own

**Release loads pre-generated Wolverine adapters from `Internal/Generated`, and they are separate
from the Api's.** `TypeLoadMode.Static` resolves pre-built types out of `opts.ApplicationAssembly`,
which here is `AiFramework.Worker`. It cannot be the Api's assembly: that needs a `Worker → Api`
reference the dependency rule forbids.

**After adding or changing a job handler — or touching `JobUserMiddleware` — regenerate:**

```bash
dotnet run --project src/Worker -- codegen write
```

Then commit the result. Debug does not need it, which is exactly the trap: stale generated code
leaves Debug green and breaks the worker at startup in Release only. `WorkerCodegenTests` catches a
*missing* adapter in the Debug suite; CI's `codegen` job re-runs the command and fails on any diff,
catching one that merely drifted. **Both trees must be regenerated** — `src/Api` too, if an event
handler changed.

Three codegen failures that compile perfectly well and only show up when you run the command:

- **A middleware parameter JasperFx cannot resolve.** It matches chain variables by exact type and
  will not upcast a concrete message to an interface, so `Before(IUserScopedJob job, ...)` fails
  with "unable to resolve a variable of type IUserScopedJob". `JobUserMiddleware` takes the
  `Envelope` and pattern-matches instead.
- **Service location.** A job handler that reuses a use case injects `ICommandDispatcher` or
  `IQueryDispatcher`, and ADR 0003's reflection-free dispatchers take `IServiceProvider`, which
  Wolverine 6 refuses under its `NotAllowed` default. `JobRegistration.IncludeJobHandlers` sets
  `ServiceLocationPolicy.AlwaysAllowed` **on this host only** — the Api keeps the strict default.
- **A middleware method Wolverine silently ignores.** `OnException` binds only when the exception
  is its FIRST parameter; `OnException(Envelope, Exception)` and `OnExceptionAsync(...)` are both
  dropped with no warning, no error, and a green build — the method simply never appears in the
  generated adapter. Measured against 6.33.0 by generating each shape and reading the output.

## Middleware method names, and the one that will cost you a production incident

`MiddlewarePolicy`'s conventions in Wolverine 6.33.0, read off the assembly rather than the docs:
`Before`/`BeforeAsync`/`Load`/`LoadAsync`/`Validate`/`ValidateAsync`, `After`/`AfterAsync`/
`PostProcess`/`PostProcessAsync`, `AfterCommit`/`AfterCommitAsync`, `Finally`/`FinallyAsync`, and
`OnException`/`OnExceptionAsync`.

They generate into this shape — confirmed by reading a real adapter, not inferred:

```csharp
try
{
    Middleware.Before(context.Envelope, /* DI services */);
    await handler.Handle(message, cancellation);
    Middleware.After(context.Envelope);          // success only: inside the try, after the call
}
catch (System.Exception exception)
{
    Middleware.OnException(exception, /* DI services */);   // NO rethrow is emitted
}
```

**The generated catch block does not rethrow, so an `OnException` method swallows the failure.**
That silently disables `OnAnyException().ScheduleRetry(...).Then.MoveToErrorQueue()` — the whole
of this host's retry and dead-lettering (ADR 0016) — for every chain the middleware touches. The
job looks successful, never retries, and never reaches the error queue. Nothing warns.

**A middleware that records failures must therefore rethrow itself**, and `throw exception;` is not
the way (CA2200 is an error here, and it erases the stack trace). Use
`ExceptionDispatchInfo.Capture(exception).Throw();`, which preserves the original stack and leaves
Wolverine's behaviour exactly as if no middleware were present.

Two smaller facts from the same measurement: a `Finally` method runs **inside** the handler
invocation, before Wolverine decides anything about retrying, and it is emitted in an inner
`try/finally` that sits *within* the outer try — so it runs BEFORE `OnException`, which makes it
useless as the place to write a failure outcome. And `Envelope` exposes no failure or exception
property at all (`Attempts`, `Id`, `MessageType` and `Status`, but nothing about the fault), so a
`Finally` that only takes the envelope cannot tell success from failure.

## It also runs the outbox pumps

`AddInfrastructure` calls `AddOutbox()`, so `OutboxPollerService` and `OutboxWorkerService` — and
every `IDomainEventHandler` they dispatch to — run **here as well as in both API replicas**. This
host is a third poller, not a replacement for the two in the API.

That is safe rather than accidental: `OutboxPoller.ClaimAsync` claims with
`FOR UPDATE SKIP LOCKED`, which is exactly the mechanism that already lets two API replicas poll
the same table. The practical effect is more outbox capacity, and one useful side effect — a
domain event that enqueues a job (`OrderPlacedConfirmationHandler`) can now be delivered by the
same process that will run the job.

Know it before it surprises you: ADR 0016 frames the outbox as running "inside the API process",
which was true when it was written and is now incomplete. Moving those pumps **off** the API is the
open follow-on; this host joining the poll is a step toward it, not the finished thing. If the
intent ever becomes "only the worker polls", that is an explicit opt-out in `AddOutbox`, not a
side effect to rely on.

## Scheduling

Scheduled jobs fire here, and only here: `Program.cs` calls `AddJobScheduling(connectionString)`
(`src/Infrastructure/Jobs/Scheduling/QuartzRegistration.cs`), which starts a clustered Quartz
scheduler over the `quartz` schema. The API registers no Quartz at all, and
`ApiHasNoSchedulerTests` keeps it that way. ADR 0017.

- **`EnqueueScheduledJob` is the only Quartz job there should ever be.** It enqueues through
  `IJobScheduler` and returns; Wolverine runs the work on its lane. A second `IJob` would be a
  second way to write a job, with no lane, retry policy or dead-letter queue.
  `[DisallowConcurrentExecution]` is on it so one schedule cannot overlap itself on a node.
- **`ScheduleSynchronizer` runs once at startup, before the scheduler starts** (its hosted service
  is registered ahead of `AddQuartzHostedService`). It writes one durable job and one cron trigger
  per scheduled job, keyed by the job's type name in the `jobs` group, and deletes anything else in
  that group. It never touches another group.
- **It preserves a pause.** A trigger whose cron is unchanged is left completely alone, so neither
  its paused state nor a pending misfire catch-up is lost on a redeploy. Only a real cron change
  rebuilds the trigger, and then re-pauses it if it was paused. There is a brief unpaused window
  in that one case; the class remarks explain why Quartz cannot close it.
- **The last build to start wins.** An old-build pod that restarts mid-rollout re-syncs the
  store to *its* schedules — deleting jobs only the new build has, reverting changed crons — and
  it stays that way until a new-build worker starts. If a schedule is missing or wrong after a
  rollout, restart a worker. ADR 0017.
- **Every node needs its own instance id.** `ProcessInstanceIdGenerator` supplies one; with
  clustering on and no generator configured, Quartz 4.1 names every node `NON_CLUSTERED` and two
  pods look like one. `SchedulingTests` asserts both halves.
- **`SchemaProvisioning.Validate` means a missing migration stops the worker.** If the worker
  refuses to start naming a `qrtz_` table, run `dotnet ef database update`; do not switch to
  `CreateIfMissing`.
- **Tracing** adds the `Quartz` activity source in `WorkerObservability`, so a trigger firing and
  the Wolverine job it enqueued share one trace. **Readiness** includes Quartz's own health check,
  from `AddQuartzHealthChecks()`.

In tests, fire a trigger with `IScheduler.TriggerJob(...)` and observe the job through a Wolverine
tracking session — never wait for a cron to come round.

## Configuration

| Key | Why it matters |
|---|---|
| `Jobs__Queues` | The lanes this pod consumes, comma-separated. **This one value is the host split** — a host listing no lane is publish-only, which is what the Api does. An unknown name fails at startup rather than leaving a queue unconsumed |
| `Jobs__LightParallelism` / `Jobs__HeavyParallelism` | Per-pod concurrency per lane. Heavy defaults to 2 deliberately; scale replicas, not this |
| `Jobs__Schedules__<JobName>` | Overrides one scheduled job's cron (Quartz syntax, seconds first). An unknown job name or an invalid cron fails startup naming the key. Only the timing is configurable; which jobs are scheduled is code |
| `Cache__Enabled` | **`false` here, in `appsettings.json` and in the manifest.** See root `CLAUDE.md`'s Jobs section |
| `Observability__ServiceName` | `aiframework-worker`, so the two hosts are distinguishable in the log store |

Double underscores, always. A single underscore binds nothing and warns nothing.

`JobOptions` is bound straight off `builder.Configuration` rather than resolved from DI, because
`AddWolverineEventPath` needs the lane list at *configuration* time — `UseWolverine` hooks the host
builder, before any service provider exists, and building a throwaway one is what ASP0000 forbids.

## Shutdown

`HostOptions.ShutdownTimeout` (5 minutes, in `Program.cs`) is paired with
`terminationGracePeriodSeconds: 300` in `k8s/base/worker.yaml`. **The two numbers are meaningless
apart.** .NET's 30-second default would abandon a heavy job long before Kubernetes was willing to;
raising only the Kubernetes side would just waste the grace period. Change one, change the other.

A SIGKILL mid-handler is not a correctness failure — the message is redelivered — but it burns a
retry attempt, the same cost `k8s/base/api.yaml` records for the outbox.

## Running it

```bash
dotnet run --project src/Worker          # port 5235, alongside the API's 5234
./scripts/worker.ps1                      # the same, plus the Docker/Postgres/port checks
./scripts/dev.ps1                        # starts Postgres, the API, this, and Vite
```

`scripts/worker.ps1` exists for the case that comes up on its own: **restarting only the worker**,
without disturbing an API and a Vite server that are working fine. `codegen write` requires that
restart before new adapters take effect, so it is routine rather than exotic. It runs in the
foreground — one process is easier to watch than to hunt for — and is option 4 in
`local-run/control-panel.bat` for anyone who would rather not open a terminal.

`Properties/launchSettings.json` sets `ASPNETCORE_ENVIRONMENT=Development`, without which
`appsettings.Development.json` never loads and the startup guard throws on an empty connection
string. That file is parsed **strictly** — no comments, confirmed by `dotnet run` rejecting one and
silently falling back to no profile at all.

## Tests

`tests/Worker.IntegrationTests`, against the real host on Testcontainers Postgres. It joins one
collection (`WorkerFactoryCollection`) so xUnit starts exactly one container, the same rule
`tests/CLAUDE.md` states for `PostgresCollection` and `ApiFactoryCollection`.

Two things a new test here has to know:

- **`IncludeExternalTransports()` is required on a tracking session.** A job goes out to a Postgres
  queue and comes back through this host's listener, and a session ignores external transports by
  default — without it every delivery assertion fails with "No messages of type … were received".
- **Do not use a tracking session to prove something did *not* happen.** `ExecuteAndWaitAsync`
  waits for a message to be handled, so a message that must never be handled only ever produces a
  timeout. Assert on the stored envelope instead: a scheduled job waits in
  `wolverine_queues.wolverine_queue_<lane>_scheduled` — its own schema, not the `wolverine` one.
