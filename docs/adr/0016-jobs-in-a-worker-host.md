# 0016. Jobs run in a worker host, with lanes as queues, on the PostgreSQL transport

**Date:** 2026-09-15
**Status:** Accepted

## Context

This repository has no concept of a job. Nothing schedules work, nothing runs work on behalf of a
request that has already returned, and there is no recurring anything. The nearest thing is the
outbox — `OutboxPollerService` and `OutboxWorkerService` — which delivers domain events raised
inside a transaction, and which runs *inside the API process*, in every one of its two replicas.

ADR 0005 already adopted Wolverine for the event path, durable on PostgreSQL, with no broker. So
the question was never "which library". It was **where the work runs**, and the stated priority
was unambiguous: do not overload the API. The deployment target is Kubernetes — `k8s/base/api.yaml`
runs two API replicas at `limits: memory 768Mi`, no CPU limit, on a chiseled image with no shell.

Four resources are shared between a `BackgroundService` and request handling, and each fails
differently. The **thread pool**: CPU-bound job work starves request threads, and the pool refills
slowly once starved, so recovery is seconds. The **GC heap against a hard limit**: a job that
buffers a report pushes Gen2 pressure onto live request threads and at worst OOMs the API pod,
killing in-flight requests. The **Npgsql pool**: shared per connection string, so a job holding
connections starves requests with no signal anywhere. And **lifecycle**: every API rollout kills
in-flight jobs, and `k8s/base/api.yaml` already records that a kill mid-handler burns a retry
attempt.

On top of that, one Deployment gets one scaling signal. The API scales on request rate; jobs scale
on queue depth. Draining a backlog would mean over-provisioning web servers, each new replica of
which starts another poller.

The tempting design — light jobs stay in the API, heavy ones go to a worker — was the one to beat.
It was rejected because "is this light enough?" is a judgment call made afresh on every new job,
against no measurement, and it drifts in one direction only.

## Decision

**Jobs run in a worker host. The API publishes and never listens.**

`src/Worker` becomes a second composition root beside `src/Api`, referencing `Application` and
`Infrastructure` and calling the same `AddInfrastructure(connectionString)`. The API registers
routing rules (`ToPostgresqlQueue`) and calls `ListenToPostgresqlQueue` for nothing. Listening is
a host-level opt-in in Wolverine — verified against the installed 6.33.0 package rather than
assumed — and that is the entire mechanism. The existing
`DisableConventionalDiscovery()` + explicit `IncludeType<T>()` posture is the enforcement point;
no new hook is needed.

**The lane is the queue; the host-to-queue mapping is configuration.** A job declares
`static abstract JobLane Lane` — `Light` or `Heavy` — which selects `jobs_light` or `jobs_heavy`.
Both are consumed by one worker Deployment on day one via `Jobs__Queues=light,heavy`, with
`MaximumParallelMessages` of 8 and 2 respectively. Splitting them onto differently-sized
Deployments later is a manifest change with no code change. The lane earns its place immediately
regardless, because it is what stops one thirty-second report blocking a queue of one-second
emails.

**A job is a message and a handler.** No `IJobRunner`, no base class, no abstraction over
Wolverine. `IJobScheduler` is an Application port implemented over `IMessageBus`, so Application never
sees a Wolverine type. Every job type is registered explicitly in `JobRegistration.cs` with a
completeness test failing the build on an omission, mirroring `AddMessaging()` exactly.

**Retry and dead-lettering are Wolverine policy** — `OnException<T>().ScheduleRetry(...)
.Then.MoveToErrorQueue()` — never hand-written backoff. The heavy lane uses `ScheduleRetry` rather
than `RetryWithCooldown`, because a cooldown occupies a listener slot for its whole duration and
one poisoned message would consume half a two-slot lane.

**Recurring jobs are self-rescheduling durable messages**: the handler schedules its own next
occurrence. Exactly one occurrence is in flight by construction, so this needs no distributed lock
and no leader election across replicas, and adds no second scheduler authority to the database.

**The worker is a web host serving only `/health` and `/health/ready`.** Kubernetes cannot probe a
non-HTTP host here: `exec` probes need a shell, and the chiseled image has none — the fact
`k8s/base/api.yaml` already records when explaining why its `preStop` uses the native sleep hook.
This also means `AddObservability()` and `RunJasperFxCommands(args)` apply to the worker unchanged.

**The transport stays PostgreSQL.** RabbitMQ is deferred with named triggers: worker replicas
sustained above four, Postgres queue polling visible in database load, a job needing priority the
transport cannot express, or a consumer outside this solution. The swap was confirmed to be a
registration change — `UseRabbitMq()` plus `ToRabbitQueue` — against the installed package.

**The worker runs with `Cache__Enabled=false`,** and a job's cache eviction is documented as a
no-op for API callers.

## Consequences

**What this makes easy.** Heavy work becomes safe to write: the worst a runaway job does is kill a
worker pod. Concurrency, retry and dead-lettering become configuration. Job capacity scales on a
signal unrelated to request rate. Deploying the API stops killing in-flight work.

**What this costs, concretely.**

- **A second Wolverine host means a second pre-generated adapter tree.** Release runs
  `TypeLoadMode.Static`, which resolves pre-built types out of `opts.ApplicationAssembly`. The
  worker needs its own `src/Worker/Internal/Generated`, its own
  `dotnet run --project src/Worker -- codegen write`, its own Debug-suite staleness guard, and its
  own diff check in CI. It cannot share the Api tree: that needs `Worker → Api`, which the
  dependency rule forbids. This is the single largest cost of the split, and ADR 0005 records
  having underestimated this exact thing once already.
- **The API does not reach zero background work, and should not.** Transactional publish means
  envelopes land in Postgres inside the request's transaction and Wolverine's durability agent
  forwards them. An insert and a send — light and bounded, but not zero. Claiming zero would be
  false.
- **A job cannot evict the API's cache.** `HybridCache` is L1-only; root `CLAUDE.md` already
  records that a write on one pod cannot evict an entry on another, which is why ingress cookie
  affinity is load-bearing (ADR 0010). A worker pod is not behind that affinity at all. The TTL,
  not the eviction, bounds how long a job's write stays invisible — the same trade ADR 0013 made
  for the catalogue, one process further out. Disabling the cache in the worker makes this
  unambiguous rather than subtle, and has the side benefit that `EvictAsync` returns on the
  `Enabled` check before it reads `ICurrentUser`.
- **`ICurrentUser` gains a second implementation with different semantics.** There is no
  `HttpContext` in the worker, so the caller travels on the job message and a middleware populates
  a scoped `JobCurrentUser`. Required by ADR 0007 as much as by the cache: ownership lives in the
  query, so a job that presents no user reads nothing.
- **A second Deployment, image stage, log stream, probe set and set of manifests.** Plus
  `terminationGracePeriodSeconds: 300` on the worker and a matching `HostOptions.ShutdownTimeout`,
  because .NET's 30s default would abandon work long before Kubernetes was willing to.
- **A job enqueued inside a request pays queue latency** it would not pay in-process, bounded by
  the transport's polling interval.
- **Both hosts are Wolverine nodes in the same `wolverine` schema,** so agent assignment and
  leader election now span API and worker pods.

**What this rules out.** Not RabbitMQ — deferred with triggers. It rules out "add a
`BackgroundService`" as the answer to future background work, which is the intended effect.

## Alternatives considered

**Keep jobs in the API process.** The honest baseline, and it costs nothing: no new project, no
second codegen tree, no second Deployment, no queue latency. The outbox pumps already prove the
shape works for light, bounded work. Rejected because the four shared resources above have no
in-process mitigation, and because the stated priority was specifically not to overload the API.

**Light jobs in the API, heavy jobs in a worker.** The strongest alternative, and the one that
keeps sub-poll-interval latency for light work while deferring the worker until the first heavy job
exists. Rejected on the rule rather than the mechanics: classifying each new job by weight is a
judgment call with no measurement behind it, made by whoever is writing the job, and it drifts one
way. "Jobs run in the worker, always" is a sentence that survives contact with a codebase. The
throughput isolation that motivated the split is preserved by keeping two *queues*, which is where
it belonged anyway.

**RabbitMQ now.** Push delivery instead of polling, prefetch backpressure, priority queues, and
queue load off the primary database — all real. Rejected as premature: it does not remove Postgres
from the path (transactional publish still lands envelopes there first), it adds a container to
compose, to the kind cluster and to the e2e gate plus credentials to `secret.yaml`, and it reverses
ADR 0005's explicit no-broker decision for capabilities this system cannot yet use. Deferred with
four named triggers rather than left to judgment.

**Hangfire.** A mature .NET job framework with a dashboard, a storage abstraction and cron
built in. Rejected as a second messaging authority: it would bring its own schema into the same
database beside EF Core's and Wolverine's — a third authority in a repo where ADR 0005 already
records the cost of the second — its own retry semantics competing with Wolverine's, and its own
serialization. Wolverine already provides durable scheduling, retry policy and dead-lettering; the
gap Hangfire would fill is a dashboard and a cron expression.

**Quartz.NET for recurring jobs.** Rejected for the same reason at smaller scale: eleven tables, a
clustering story, and a schema authority, to buy a cron expression that a self-rescheduling durable
message provides with no lock and no new tables.

**A timer-based `IHostedService` in the worker for recurring work.** Simpler to read than
self-rescheduling messages. Rejected because every replica fires it, so it needs a distributed lock
or leader election to be correct above one replica — and the whole point of the worker is that its
replica count is free to change.

**Extend the existing hand-built outbox to carry jobs.** It already claims with
`FOR UPDATE SKIP LOCKED`, leases, retries and dead-letters, and it is fully understood by its
author. Rejected because it has no scheduling, no lanes and no concurrency control per message
type, and adding all three would rebuild what Wolverine already provides and ADR 0005 already paid
for. The outbox stays what it is: the domain-event delivery path.
