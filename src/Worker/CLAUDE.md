# Worker

The job host. A second composition root beside `src/Api`, running the same `AddInfrastructure`
against the same database — the only difference is which Wolverine queues it listens on.

See ADR 0016, and the `jobs` skill for the rules that apply wherever a job is written — lanes,
enqueueing, retry, Quartz, and configuration keys. This file is what is specific to *this project*.

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

**Release loads pre-generated Wolverine adapters from `Internal/Generated`, separate from the
Api's.** After adding or changing a job handler — or touching `JobUserMiddleware` — run
`dotnet run --project src/Worker -- codegen write`, commit the result, and restart the worker. The
`regenerate` skill has why the trees cannot be shared, the Debug-green/Release-broken trap, and
two codegen failures that compile cleanly (the interface-typed middleware parameter, and service
location under `NotAllowed`).

One more codegen failure specific to middleware here:

- **A middleware method Wolverine silently ignores.** `OnException` binds only when the exception
  is its FIRST parameter. `OnException(Envelope, Exception)` is dropped with no warning, no error,
  and a green build — the method simply never appears in the generated adapter. The async spelling
  is fine: `OnExceptionAsync(Exception, …)` binds and is awaited. Measured against 6.33.0 by
  generating each shape and reading the output.

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

**Async works throughout, and the parameter order is the only real constraint.**
`BeforeAsync`/`AfterAsync`/`OnExceptionAsync` are all generated with `await … .ConfigureAwait(false)`,
so a recorder that writes to the database needs no sync-over-async anywhere. What is NOT negotiable
is that the exception comes first in `OnException`; everything after it binds from DI like any
other middleware parameter.

Two smaller facts from the same measurement: a `Finally` method runs **inside** the handler
invocation, before Wolverine decides anything about retrying, and it is emitted in an inner
`try/finally` that sits *within* the outer try — so it runs BEFORE `OnException`, which makes it
useless as the place to write a failure outcome. And `Envelope` exposes no failure or exception
property at all (`Attempts`, `Id`, `MessageType` and `Status`, but nothing about the fault), so a
`Finally` that only takes the envelope cannot tell success from failure.

## It writes the outbox but never delivers it

The worker raises domain events like any host — a warehouse's `RecordShipment` raises
`OrderShipped` here, and `DomainEventsInterceptor` writes the outbox row in the same transaction —
but it runs **no outbox pump**. `AddInfrastructure` registers the outbox's services through
`AddOutbox()`; the two pumps come from `AddOutboxPumps()`, which only the Api's `Program.cs` calls.
An API replica delivers the row, within one poll interval.

The reason is the notifiers. The pumps run every `IDomainEventHandler`, and a notifier pushes the
row it writes through `INotificationPush`, whose only implementation is SignalR's, in the Api. This
host polled until 2026-10-02, and every event it claimed was written to the feed with nobody pushed.
ADR 0028.

- **Do not call `AddOutboxPumps()` here** to get more outbox capacity or to move the pumps off the
  API (ADR 0016's open follow-on). Either needs a push path that works from any process first —
  ADR 0028 records `LISTEN`/`NOTIFY` as that path. `OutboxPumpTests` fails if the worker gains one.
- **A worker test that needs an event delivered drains it by hand**:
  `WorkerFactory.DrainOutboxUntilEmptyAsync()`, the same loop as `ApiFactory`'s.

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

The keys (`Jobs__Queues`, the parallelism pair, `Jobs__Schedules__<JobName>`, `Cache__Enabled`,
`Observability__ServiceName`) are tabled in the `jobs` skill. `Cache__Enabled` is `false` here
in **both** `appsettings.json` and `k8s/base/worker.yaml` — change one, change the other.

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

`tests/Worker.IntegrationTests`, against the real host on Testcontainers Postgres, in one
`WorkerFactoryCollection`. Read `tests/CLAUDE.md`'s "Worker tests" section before writing one —
tracking sessions need `IncludeExternalTransports()`, and must never be used to prove something
did *not* happen.
