# A job framework: lanes are queues, work runs in a worker host

**Date:** 2026-09-15
**Status:** Approved, not yet implemented

## Context

This repository has no concept of a job. Nothing schedules work, nothing runs work on behalf of
a request that has already returned, and there is no recurring anything. The two things that
come closest are neither of them a job framework:

- `Outbox/` — `OutboxPollerService` and `OutboxWorkerService`, two `BackgroundService` pumps that
  deliver **domain events** that were raised inside a transaction. They run *inside the API
  process*, in every one of its replicas.
- `EventPath/WolverineEventPath.cs` — ADR 0005's spike. Durable Wolverine on PostgreSQL, envelope
  storage in its own `wolverine` schema, `UseDurableLocalQueues()`, discovery disabled with one
  handler explicitly included.

So the question is not "which library" — Wolverine is already here, already durable, already
paying for its own schema authority and its own codegen build step. The question is **where the
work runs**, and the stated priority is unambiguous: *do not overload the API*.

### The deployment target is Kubernetes

`k8s/base/api.yaml` runs the API at **two replicas**, `requests: cpu 200m`, `limits: memory
768Mi`, no CPU limit, `terminationGracePeriodSeconds: 60`, `readOnlyRootFilesystem: true`, and a
`preStop` sleep using the **native** sleep hook because — the manifest's own comment — *the
chiseled base image has no shell*. That single fact decides more of this design than it looks
like it should; see "The worker is a web host that serves only health endpoints" below.

Everything in this spec is therefore written against a cluster, not against `dotnet run`.
`docker compose` remains the inner loop and the worker must work there too, but the shape is
chosen for Kubernetes.

### What was verified, rather than assumed

The API surface below was read out of the XML documentation shipping inside the installed
6.33.0 packages (`~/.nuget/packages/wolverinefx*/6.33.0/lib/net10.0/*.xml`), not recalled:

| Member | Package | Why this design needs it |
|---|---|---|
| `ListenToPostgresqlQueue(WolverineOptions, string)` | `WolverineFx.Postgresql` | **Listening is a host-level opt-in.** This is the entire mechanism of the design |
| `PostgresqlListenerConfiguration.MaximumMessagesToReceive` / `PollingInterval` / `CircuitBreaker` | `WolverineFx.Postgresql` | Per-queue throughput and backpressure |
| `IListenerConfiguration<T>.MaximumParallelMessages(int)` / `ListenerCount(int)` / `UseDurableInbox` | `WolverineFx` | Per-lane concurrency, set differently for light and heavy |
| `ToPostgresqlQueue(IPublishToExpression, string)` | `WolverineFx.Postgresql` | Routing a message type to a queue **without handling it** |
| `MessageBusExtensions.ScheduleAsync<T>(IMessageBus, T, DateTimeOffset\|TimeSpan, DeliveryOptions)` | `WolverineFx` | Durable delayed delivery — the basis for recurring jobs |
| `ConfiguredMessageExtensions.ScheduledAt<T>` / `DelayedFor<T>` | `WolverineFx` | The same, expressed on an outgoing message |
| `OnException<T>().RetryWithCooldown(TimeSpan[])` / `.ScheduleRetry(...)` / `.MoveToErrorQueue()` | `WolverineFx` | Retry as policy, replacing hand-written backoff arithmetic |
| `DurabilityMode.Solo \| Balanced \| MediatorOnly \| Serverless` | `WolverineFx` | The API/worker durability split |
| `UseRabbitMq(...)`, `ToRabbitQueue(...)`, `RabbitMqListenerConfiguration.PreFetchCount(ushort)` | `WolverineFx.RabbitMq` | ADR 0005's claim that a transport swap is a registration change — confirmed, it is |

### Why in-process loses

Four resources are shared between a `BackgroundService` and request handling, and each fails
differently:

- **The thread pool.** CPU-bound or blocking job work starves request threads from the same pool,
  and the pool adds threads slowly once starved, so recovery is measured in seconds. There is no
  in-process mitigation short of a dedicated scheduler.
- **The GC heap, against a hard limit.** A job that buffers a report pushes Gen2 and LOH pressure
  onto live request threads, and at worst OOMs the API pod at `768Mi`, killing in-flight requests.
- **The Npgsql pool.** Requests, outbox workers and job handlers share one pool per connection
  string. A job holding connections across long work starves requests with no signal anywhere.
- **Lifecycle.** Every API rollout kills in-flight jobs. `k8s/base/api.yaml`'s own comment already
  notes that a kill mid-handler burns an attempt against `MaxAttempts`.

And the scaling coupling: one Deployment gets one scaling signal. The API scales on request rate;
jobs scale on queue depth. Today, draining a backlog would mean over-provisioning web servers —
each new replica of which starts another poller.

### Why "light vs heavy" is not a hosting decision

The tempting design is "light jobs stay in the API, heavy ones go to a worker". It was rejected.
"Is this light enough?" is a judgment call made afresh on every new job, by whoever is writing it,
against no measurement — and that rule drifts in one direction only. A rule that can be stated in
one sentence and grepped in one file survives contact with a codebase; a rule that requires
judgment does not.

The axis that *is* worth keeping is throughput isolation: a slow job must not head-of-line block
a fast one. That is a **queue** concern, not a **process** concern, and separating the two is what
makes the rest of this design fall out.

## Decisions

### Jobs run in a worker host. The API publishes and never listens

`src/Worker` becomes a second composition root, alongside `src/Api`. It references `Application`
and `Infrastructure` and calls the same `AddInfrastructure(connectionString)`. It maps no
controllers and owns no feature code.

The API registers **routing rules only**:

```csharp
opts.PublishMessage<RebuildOrderReport>().ToPostgresqlQueue(JobLanes.Heavy);
```

and calls `ListenToPostgresqlQueue` for nothing. The worker calls it for the queues named in its
configuration. Same handler code, same assembly graph, same `AddInfrastructure`; the only
difference between the two hosts is which queues they listen to.

**The enforcement point already exists and needs no new hook.** `WolverineEventPath` runs
`opts.Discovery.DisableConventionalDiscovery()` with explicit `IncludeType<T>()` calls. A job
handler that is not named in the API's discovery cannot run in the API, and the list of what each
host handles is a handful of greppable lines in one file per host.

**One honest correction to "the API runs nothing in the background."** It still runs Wolverine's
durability agent, and it must: transactional publish means envelopes land in Postgres inside the
request's transaction, and something has to forward them to the queue. That is an insert and a
send — genuinely light, and bounded — but it is not zero, and claiming zero would be false.

### The lane is the queue; the host-to-queue mapping is configuration

Two lanes, two queues, from day one:

| Lane | Queue | Shape of work | Listener configuration |
|---|---|---|---|
| `Light` | `jobs-light` | Milliseconds to a couple of seconds, one or two round trips, negligible CPU. Sending mail, nudging a webhook, writing an audit row | `MaximumParallelMessages(8)` |
| `Heavy` | `jobs-heavy` | Seconds to minutes, CPU- or memory-bound, or fanning over a large result set. Report generation, bulk import, recalculation | `MaximumParallelMessages(2)` |

Both queues are consumed by **one** worker Deployment on day one, selected by
`Jobs__Queues=light,heavy`. The lane split still earns its place immediately, because it is what
stops one thirty-second report from blocking a queue of one-second emails behind it.

When the heavy lane outgrows the light one, the change is a second Deployment with
`Jobs__Queues=heavy` and different `resources`, and the first one narrowed to
`Jobs__Queues=light`. **No code changes.** That deferral is the point of putting the lane on the
message and the queue-to-host mapping in configuration, rather than hard-coding either.

`MaximumParallelMessages(2)` on the heavy lane is deliberately low. Parallelism above the pod's
available cores on CPU-bound work buys nothing and costs context switching and GC pressure; the
number to tune is the *replica count*, not the per-pod parallelism.

### A job is a message and a handler, not a new abstraction over Wolverine

There is no `IJobRunner`, no `JobContext`, no base class. A job is:

```csharp
// src/Application/Jobs/RebuildOrderReport.cs
public sealed record RebuildOrderReport(Guid OwnerId, DateOnly Month) : IJob
{
    public static JobLane Lane => JobLane.Heavy;
}
```

`IJob` carries a **static abstract** member, so the lane is readable generically with no
reflection and no instance — the same reflection-free posture `AddCommand`/`AddQuery` already
hold, and the reason `Infrastructure/CLAUDE.md` tells you not to "simplify" dispatch into
`MakeGenericType`:

```csharp
// src/Application/Abstractions/Jobs.cs
public enum JobLane { Light, Heavy }

public interface IJob
{
    static abstract JobLane Lane { get; }
}
```

The handler is a plain Wolverine handler, discovered by explicit `IncludeType<T>()` exactly as
`OrderPlacedNotificationHandler` already is. A job handler is free to resolve
`ICommandDispatcher`/`IQueryDispatcher` and go through the existing request pipeline — that is the
expected shape for anything that touches domain state, because it is what keeps validation, the
unit of work and the logging behavior on the path.

### Enqueuing is an Application port; Wolverine stays behind it

```csharp
// src/Application/Abstractions/Jobs.cs
public interface IJobQueue
{
    Task EnqueueAsync<TJob>(TJob job, CancellationToken cancellationToken) where TJob : IJob;

    Task ScheduleAsync<TJob>(TJob job, DateTimeOffset at, CancellationToken cancellationToken)
        where TJob : IJob;

    Task ScheduleAsync<TJob>(TJob job, TimeSpan delay, CancellationToken cancellationToken)
        where TJob : IJob;
}
```

Implemented once in `Infrastructure/Jobs/JobQueue.cs` over `IMessageBus`. Application never sees a
Wolverine type — that is the dependency rule, and it is also what keeps ADR 0005's "the seam stays
ours" promise true for jobs as well as for events.

**Every job type must be registered explicitly** in `Infrastructure/Jobs/JobRegistration.cs`,
mirroring `AddMessaging()`, and a completeness test fails the build when an `IJob` in the
Application assembly has no registration — exactly as `RegistrationCompletenessTests` already does
for commands and queries. That test is the only thing standing in for compile-time safety, and it
must never be deleted.

### A job that must not be lost is enqueued from a domain event handler

A job enqueued by a command that then fails must not run. The obvious mechanism is Wolverine's EF
Core outbox integration (`opts.UseEntityFrameworkCoreTransactions()`, already configured).
**It was measured, and it does not fit this codebase's command pipeline.**

Three cases, run against a real Postgres via `JobEnqueueAtomicitySpike` (2026-09-15):

| Case | Result |
|---|---|
| Publish through `IDbContextOutbox<AiFrameworkDbContext>`, then plain `context.SaveChangesAsync()` | **Throws.** `InvalidOperationException: The configured execution strategy 'NpgsqlRetryingExecutionStrategy' does not support user-initiated transactions` |
| The same, wrapped in `Database.CreateExecutionStrategy().ExecuteAsync(...)` | **Persists nothing.** `wolverine_outgoing_envelopes` is unchanged; the message is lost when the scope falls away |
| `outbox.SaveChangesAndFlushMessagesAsync(...)` inside the execution strategy | Persists and delivers — asynchronously, via `wolverine_incoming_envelopes` |

The first result is the decisive one and it was not anticipated. **Resolving the outbox and
publishing enrolls the `DbContext`, which opens a transaction** — so `UnitOfWork.SaveChangesAsync`,
a plain `SaveChangesAsync`, cannot commit it at all. This is not "the envelope is delivered
later"; the commit itself fails.

Using the EF Core outbox for jobs would therefore require **both** changes to every command in the
system: wrapping the unit-of-work commit in an execution strategy, *and* replacing
`context.SaveChangesAsync` with `outbox.SaveChangesAndFlushMessagesAsync`. That is a rewrite of the
commit path to add a job framework, and it is refused on those grounds.

**The mechanism is therefore the existing domain-event path:**

```
handler raises a domain event
  -> DomainEventsInterceptor writes the outbox row in the SAME SaveChangesAsync (same transaction)
     -> OutboxWorkItemProcessor delivers it
        -> IDomainEventHandler<T> calls IJobQueue.EnqueueAsync
```

Transactional by construction, at-least-once with the dedupe key `DomainEventContext` already
carries, and **zero change to `UnitOfWork` or the command pipeline**. The cost is one hop and a
domain event per guaranteed job, which is a far smaller price than rewriting every commit.

`IJobQueue` itself publishes through plain `IMessageBus`, which is correct for both of its callers:
a domain event handler (already past commit, so there is nothing left to be atomic with) and a job
handler rescheduling itself.

**The rule this produces, and it belongs in `CLAUDE.md`:** a job enqueued *directly* from a
command handler is fire-and-forget and will run even if the command later fails. For a job that
must not be lost, raise a domain event and enqueue from its handler. `IJobQueue`'s XML docs say
this at the point of use, because it is exactly the kind of thing that is invisible until it is
a production incident.

### Retry and dead-lettering are Wolverine policy, never hand-written backoff

```csharp
opts.OnException<TransientJobException>()
    .RetryWithCooldown(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

opts.Policies.OnAnyException()
    .ScheduleRetry(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30))
    .Then.MoveToErrorQueue();
```

`RetryWithCooldown` holds the message in memory across the delay; `ScheduleRetry` puts it back in
durable storage for later. **Heavy-lane jobs use `ScheduleRetry`, not `RetryWithCooldown`** — a
cooldown occupies a listener slot for its whole duration, which on a lane with
`MaximumParallelMessages(2)` means one poisoned message can consume half the lane's capacity.

Nothing in `Jobs/` reimplements `OutboxOptions`-style `NextAttemptAt` arithmetic. That code exists
in `Outbox/` and stays there; the whole point of routing jobs through Wolverine is not to write it
a second time.

### Recurring jobs are self-rescheduling durable messages

Wolverine has durable scheduled delivery and no cron. The pattern is that the handler schedules
its own next occurrence as its final act:

```csharp
public sealed class PruneStaleCartsHandler(IJobQueue jobs, IClock clock)
{
    public async Task Handle(PruneStaleCarts message, CancellationToken cancellationToken)
    {
        // ... the work ...

        await jobs.ScheduleAsync(new PruneStaleCarts(), TimeSpan.FromHours(1), cancellationToken);
    }
}
```

**Exactly one occurrence is in flight by construction**, so this needs no distributed lock and no
leader election across worker replicas — which is the whole reason it beats a timer-based
`IHostedService`, where every replica would fire. It also adds no second scheduler authority to
the database, which matters in a repo whose ADR 0005 already records the cost of Wolverine owning
tables outside `dotnet ef migrations`.

The schedule is seeded idempotently at worker startup. Quartz.NET was rejected: a second scheduler
with its own eleven tables, its own clustering story and its own schema authority, to buy a cron
expression.

### The worker is a web host that serves only health endpoints

A `Microsoft.NET.Sdk.Worker` host exposes nothing over HTTP, and Kubernetes then has no way to
probe it: `exec` probes need a shell, and `k8s/base/api.yaml` already records that **the chiseled
base image has no shell** — which is why its `preStop` uses the native sleep hook. Adding a shell
to get probes back would mean giving up the chiseled image.

So `src/Worker` is `Microsoft.NET.Sdk.Web`, built on `WebApplication`, mapping exactly two
endpoints and no controllers:

- `/health` — liveness. Never touches Postgres, same contract as the API's.
- `/health/ready` — readiness, via `AddDbContextCheck<AiFrameworkDbContext>`.

Three things fall out of this that are worth having on purpose:

- `builder.AddObservability()` works **unchanged**. It is a `WebApplicationBuilder` extension
  living in `src/Api/Observability/`, and a non-web worker would have forced it down into
  Infrastructure as an `IHostApplicationBuilder` extension first. Instead it is copied to
  `src/Worker/Observability/` only if it must be; the preferred route is moving it to
  Infrastructure as a shared `WebApplicationBuilder` extension, since both hosts are web hosts.
- The worker image reuses `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`, so
  `Dockerfile.api` grows one stage rather than gaining a sibling file.
- `RunJasperFxCommands(args)` applies identically, which the worker needs for its own
  `codegen write` (below).

### The worker has no `HttpContext`, so the caller travels on the job

`ICurrentUser` is registered in `src/Api/Program.cs` over `IHttpContextAccessor`. There is no
`HttpContext` in the worker, and three places in `Behaviors.cs` read `ICurrentUser` — including
`EvictAsync`, which **throws** `InvalidOperationException` when a command implements
`IInvalidatesCache` and no caller is resolvable.

A job therefore carries the user it acts on behalf of, as an ordinary property of the message, and
the worker registers a scoped `JobCurrentUser` that a Wolverine middleware populates from the
message before the handler runs. This is not only about the cache: ADR 0007 puts ownership *in the
query*, so a job that reads or writes a user's orders must present that user or read nothing.

### The cache is off in the worker, and a job cannot evict the API's cache

`HybridCache` here is **L1 only**. Root `CLAUDE.md` already records what that means across pods:
"a write handled by one pod cannot evict an entry held by the other", which is why cookie affinity
on the `aiframework-api` Ingress is load-bearing (ADR 0010).

A worker pod is not behind that affinity and is not an API pod at all. So:

> **A job's cache eviction reaches only the worker's own L1 cache, which is empty. It is a no-op
> for every API caller. The TTL, not the eviction, is what bounds how long a job's write stays
> invisible.**

This is the same accepted trade ADR 0013 already made for the global catalogue, one process
further out. Rather than leave it as a subtle trap, the worker runs with **`Cache__Enabled=false`**.
That has a second, concrete benefit: `EvictAsync` checks `CacheOptions.Enabled` and returns
*before* it reads `ICurrentUser`, so a job dispatching an `IInvalidatesCache` command cannot throw
on a missing caller even if the middleware above ever fails to populate one.

### The transport stays PostgreSQL. RabbitMQ has named trigger conditions

ADR 0005 chose "no broker" deliberately, and the whole local story depends on it: one container in
`docker-compose.yml`, one `postgres.yaml` in the cluster, one set of credentials.

RabbitMQ does **not** remove Postgres from this path. Transactional publish still lands envelopes
in Postgres first; the broker is an additional hop, an additional failure mode, a container in
compose *and* in the kind cluster *and* in the e2e gate, and credentials in `secret.yaml`.

What it would eventually buy is real: push delivery instead of poll latency, prefetch-based
backpressure (`PreFetchCount`), priority queues, and taking queue polling load off the primary
database. The conditions under which that becomes worth its cost, written down now so the decision
is not made on vibes later:

1. Worker replicas sustained above **four**, at which point every replica polling Postgres is a
   measurable share of database load.
2. Postgres queue polling visible in database load or lock contention under normal operation.
3. A job needing priority or fairness the Postgres transport cannot express.
4. A consumer outside this solution needing to receive these messages.

When one of those holds, the change is `UseRabbitMq()` plus swapping `ToPostgresqlQueue` for
`ToRabbitQueue` in `JobRegistration.cs` — verified against the installed package, not assumed.
Building the lane abstraction now is what keeps it that small.

### The worker is a second Wolverine host, and therefore a second codegen tree

This is the largest concrete cost of the split and it is the one ADR 0005 would want stated
plainly, having underestimated exactly this last time.

Release runs `TypeLoadMode.Static`, which resolves pre-built handler types out of
`opts.ApplicationAssembly`. Two hosts means two application assemblies, so the worker needs:

- its own `src/Worker/Internal/Generated`, committed;
- its own `dotnet run --project src/Worker -- codegen write`;
- its own staleness guard in the Debug suite, mirroring `WolverineCodegenTests`;
- its own diff check in CI's `codegen` job.

The worker **cannot** point `ApplicationAssembly` at the Api assembly to share one tree: that
needs `Worker → Api`, which the dependency rule forbids.

### Observability: a second service, one trace

`Observability:ServiceName` is `aiframework-worker` in the worker's `appsettings.json`, so the two
hosts are distinguishable in the log store. Everything else is unchanged —
`Observability__Otlp__Enabled`/`__Endpoint` are the same double-underscore keys on the same
`app-config` ConfigMap.

Trace continuity across the job boundary works the way the outbox's already does: Wolverine
propagates W3C trace context on its envelopes, and `ObservabilityRegistration` already calls
`.AddSource("Wolverine")`. A job's logs therefore carry the `TraceId` of the request that enqueued
it, which is the property that makes the whole thing debuggable.

`Behaviors.LoggedAsync` covers any command or query a job dispatches, unchanged. A job handler
**does not** log "handling X" or "returning failure" by hand, for the same reason no other handler
does.

## Kubernetes

```yaml
# k8s/base/worker.yaml — no Service: nothing routes to it, and httpGet probes
# address the pod directly.
kind: Deployment
metadata: { name: worker }
spec:
  replicas: 1
  template:
    spec:
      # Must exceed the longest expected heavy-lane job. A SIGKILL mid-handler is not a
      # correctness failure — the message is redelivered — but it does burn a retry
      # attempt, exactly as k8s/base/api.yaml records for the outbox.
      terminationGracePeriodSeconds: 300
      containers:
        - name: worker
          image: aiframework-worker:local
          env:
            - { name: Jobs__Queues,  value: 'light,heavy' }
            - { name: Cache__Enabled, value: 'false' }
          resources:
            requests: { cpu: 500m, memory: 512Mi }
            limits:   { memory: 1536Mi }
          securityContext:
            readOnlyRootFilesystem: true   # Release loads pre-generated adapters; nothing writes
            runAsNonRoot: true
            capabilities: { drop: ['ALL'] }
```

Four Kubernetes-specific notes:

- **No `Service`, and no `preStop` sleep.** The API needs both because it must leave the Service's
  endpoints before it stops accepting connections. Nothing routes to the worker, so there are no
  endpoints to drain; what matters instead is that Wolverine stops listening and finishes
  in-flight messages, which is host shutdown. `HostOptions.ShutdownTimeout` is raised to match
  `terminationGracePeriodSeconds` — the .NET default is 30s and would otherwise abandon work long
  before Kubernetes was willing to.
- **Higher memory limit than the API, and still no CPU limit.** Same reasoning
  `k8s/base/api.yaml` already gives: memory is the one the kubelet cannot reclaim gracefully, so a
  ceiling turns a leak into a restart; a CPU limit only throttles work the request already
  guarantees a share of.
- **Both hosts are Wolverine nodes in the same `wolverine` schema.** Durability mode stays
  `Balanced`, so agent assignment and leader election span API and worker pods. A pod dying leaves
  a node record that Wolverine's own health checking reclaims — the same class of behaviour as
  `OutboxOptions.LeaseDuration`, and worth knowing before it is discovered by surprise in a rolling
  update.
- **Autoscaling on queue depth is out of scope, and the path is recorded.** An HPA cannot read
  queue depth natively; KEDA's `postgresql` scaler running a `COUNT` against the Wolverine queue
  table is the intended route. The local kind cluster stays at `replicas: 1` — this repo's kind
  cluster is already the largest thing on the machine once `-WithObservability` is on.

## Configuration

`src/Worker/appsettings.json`:

```json
{
  "ConnectionStrings": { "Default": "" },
  "Cache": { "Enabled": false },
  "Observability": { "ServiceName": "aiframework-worker", "Otlp": { "Enabled": false } },
  "Jobs": {
    "Queues": "light,heavy",
    "Light": { "MaximumParallelMessages": 8 },
    "Heavy": { "MaximumParallelMessages": 2 }
  }
}
```

`Jobs:Queues` is a comma-separated list rather than an array because it is set from a Kubernetes
`env` value, and **double underscores** are the binding convention here — `Jobs__Queues`, never
`Jobs_Queues`, which binds nothing and warns nothing. Root `CLAUDE.md` records that trap for
`Cache__Enabled` and `ForwardedHeaders__Enabled`; this is the same one.

`JobOptions` is validated at startup: an unknown lane name in `Queues` fails loudly rather than
leaving a queue silently unconsumed, which is the same failure mode `OutboxOptions`' own
`WorkerCount >= 1` validation exists to prevent.

## Testing

| Project | Covers |
|---|---|
| `tests/Application.Tests` | Job handlers with `IJobQueue` and ports substituted. No Wolverine |
| `tests/Infrastructure.Tests` | `JobQueue` over a substituted `IMessageBus`; lane→queue mapping; **job registration completeness**; `JobOptions` validation |
| `tests/Worker.IntegrationTests` | The worker host boots against Testcontainers Postgres; a published job reaches its handler on the right queue; the codegen staleness guard |
| `tests/Api.IntegrationTests` | **The API listens to no job queue** — asserted against `ServiceCapabilities.MessagingEndpoints`, the same route `WolverineLocalQueueDurabilityTests` already uses to prove local queues are durable |

That last one is the test that makes this design a rule rather than an intention, and it is the
one to write first.

No test waits on a real delay. Scheduled delivery is asserted by inspecting the scheduled envelope,
not by sleeping until it fires — the same standing rule `ApiFactory.cs` already enforces.

## Consequences

**What this makes easy.** Heavy work becomes safe to write: the worst a runaway job can do is kill
a worker pod. Job concurrency, retry policy and dead-lettering become configuration. Scaling job
capacity is a replica count that has nothing to do with request rate. Deploying the API no longer
kills in-flight work. And splitting the lanes onto differently-sized pods later needs no code.

**What this costs.**

- A second codegen tree, a second `codegen write`, a second CI check, and a second staleness test.
  This is the big one; see above.
- A second Deployment, image build stage, log stream, probe set and set of manifests.
- A job enqueued inside a request pays queue latency it would not pay in-process. Bounded by the
  Postgres transport's polling interval, and irrelevant for everything a job should be used for.
- One more place that can be out of date relative to the database. The worker runs the same
  migrations as the API but is rolled independently.
- `ICurrentUser` now has two implementations with meaningfully different semantics, and the second
  one exists because the first one cannot work outside a request.

**What this rules out.** Not RabbitMQ — deferred with named triggers. It does rule out
"just add a `BackgroundService`" as the answer to future background work, which is the intended
effect.

## Accepted risks

- **A job cannot evict the API's cache.** Bounded by the TTL, documented above, and made
  unambiguous by disabling the cache in the worker entirely.
- **`Jobs__Queues` can be misconfigured such that a queue has no consumer.** Startup validation
  catches an unknown lane name; it cannot catch a *deployment* where no pod lists a valid lane.
  Queue depth monitoring is the answer, and it is the same thing KEDA would need.
- **The self-rescheduling recurring pattern loses its schedule if every occurrence
  dead-letters.** The chain is the schedule, so a terminally failing recurring job stops
  recurring. This is a deliberate trade against a timer that fires regardless of whether anything
  works; the mitigation is that dead-lettering logs at `Warning`, which is the level root
  `CLAUDE.md` reserves for what an operator actually wants to see.

## Out of scope

- **Moving the outbox pumps to the worker.** They stay in the API. This is the obvious follow-on
  and it also retires ADR 0005's "two paths running side by side", but it is a separate change
  against `OutboxDeliveryTests` and the API's `terminationGracePeriodSeconds` reasoning.
- **RabbitMQ.** Triggers recorded above.
- **KEDA and queue-depth autoscaling.** Path recorded above.
- **A job dashboard or admin UI.** Queue depth is a SQL query; that is enough for now.
- **Event sourcing.** Still deferred, per ADR 0005.

## Files

**Created:**
- `src/Application/Abstractions/Jobs.cs` — `JobLane`, `IJob`, `IJobQueue`
- `src/Application/Jobs/` — the reference job
- `src/Infrastructure/Jobs/JobOptions.cs`, `JobQueue.cs`, `JobRegistration.cs`, `JobCurrentUser.cs`
- `src/Worker/` — `Program.cs`, `appsettings.json`, `AiFramework.Worker.csproj`, `CLAUDE.md`,
  `Internal/Generated/`
- `k8s/base/worker.yaml`
- `tests/Worker.IntegrationTests/`
- `docs/adr/0016-jobs-in-a-worker-host.md`
- `.claude/commands/job.md`

**Modified:**
- `src/Infrastructure/EventPath/WolverineEventPath.cs` — lane routing, listener configuration,
  error policies, and the host's listen/publish split
- `src/Infrastructure/InfrastructureRegistration.cs` — `AddJobs()`
- `src/Api/Program.cs` — publish-only routing
- `Dockerfile.api` — a `worker` stage
- `AiFramework.slnx`, `.github/workflows/ci.yml`, `deploy/deploy.ps1`
- `.claude/hooks/hooks.config.json` **and** `.claude/hooks/dependency-rule.ps1` — `Worker` as a
  layer. **Both files**: the script's own comment warns that adding a layer to the config without
  a matching `$banned` entry makes the path match with an empty banned list, silently disarming it
- `CLAUDE.md` — a `## Jobs` section
- `docs/adr/0005-wolverine-for-the-event-path.md` — a pointer to 0016
