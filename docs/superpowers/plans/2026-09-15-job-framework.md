# Job Framework Implementation Plan

> **For agentic workers:** Steps use checkbox (`- [ ]`) syntax for tracking. Implement
> task-by-task, verifying each before starting the next.

**Goal:** Background work has one shape, and it does not run in the API. A job is a message with a
lane; the lane is a queue; the worker listens, the API only publishes. Retry, scheduling and
dead-lettering come from Wolverine rather than from hand-written arithmetic.

**Architecture:** `src/Worker` as a second composition root, a web host serving only health
endpoints. Two PostgreSQL queues (`jobs_light`, `jobs_heavy`) with different
`MaximumParallelMessages`, selected per host by `Jobs__Queues`. `IJob` carries
`static abstract JobLane Lane`; `IJobScheduler` is an Application port over `IMessageBus`. No broker.

**Tech Stack:** .NET 10 (net10.0), `LangVersion` 14.0, Wolverine 6.33.0 (`WolverineFx`,
`.Postgresql`, `.EntityFrameworkCore`), EF Core + Npgsql 10.0.3, xUnit + FluentAssertions +
NSubstitute, Testcontainers. Kubernetes via kind.

**Spec:** `docs/superpowers/specs/2026-09-15-job-framework-design.md`
**ADR:** `docs/adr/0016-jobs-in-a-worker-host.md`

> **Names that changed during implementation**, so this plan still reads correctly against the
> code: `IJobQueue` became **`IJobScheduler`** (CA1711 reserves the `Queue` suffix, and the new
> name sits better beside `ICommandDispatcher`/`IQueryDispatcher`), and the queue names use
> underscores — **`jobs_light`/`jobs_heavy`** — because the Postgres transport sanitises a name
> into an identifier and the real endpoints are `postgresql://jobs_light/`. The reference jobs live
> in their feature folder (`src/Application/Orders/`), not a technical `Jobs/` one, matching how
> this repo organises Application. Task 1 additionally changed the enqueue MECHANISM — see its
> result block.

## Global Constraints

- **Warnings are errors** — compiler, analyzers and build. Never suppress without a narrow
  `#pragma warning disable`/`restore` pair carrying a justification comment above it.
- **Nullable is enabled.** **Never `catch (Exception)`** — CA1031 is an error outside the outbox's
  file-scoped exemption in `.editorconfig`. `throw;`, never `throw ex;`. A job handler is **not**
  a new exemption: Wolverine's error policies receive the exception, so there is nothing to catch.
- **The dependency rule is hook-enforced.** `Worker` may reference `Application` and
  `Infrastructure` (DI only) and must never reference `Api`. No Wolverine type may appear in
  `src/Application` — `IJobQueue` is the seam.
- **Every job type must be registered** in `Infrastructure/Jobs/JobRegistration.cs`. The
  completeness test fails the build otherwise, exactly as `AddMessaging()`'s already does.
- **Handlers never call `SaveChangesAsync`.** Unchanged by this plan.
- **`required` in Domain, never `[Required]`.** No DataAnnotations outside Api DTOs.
- **Config keys use double underscores** — `Jobs__Queues`, `Cache__Enabled`. A single underscore
  binds nothing and warns nothing.
- **After adding or changing a Wolverine handler, regenerate adapters.** There are now **two**
  trees: `dotnet run --project src/Api -- codegen write` and
  `dotnet run --project src/Worker -- codegen write`. Commit both.
- **No secrets in `appsettings*.json`.** `.claude/hooks/no-secrets.ps1` blocks the alternative.
- **No `Thread.Sleep`, no `Task.Delay`, no retry-until-timeout in tests.** Scheduled delivery is
  asserted by inspecting the scheduled envelope, never by waiting for it.
- Run `dotnet test <project>` **one project at a time** — two paths in one invocation fails with
  MSB1008.
- Dev connection string for `dotnet ef` and for local host runs:
  `Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres`
- Branch: `claude/job-framework-worker-host`.

---

## File Structure

**Created:**
- `src/Application/Abstractions/Jobs.cs` — `JobLane`, `IJob`, `IUserScopedJob`, `IJobScheduler`
- `src/Application/Jobs/RebuildOrderReport.cs` — the reference job and its handler
- `src/Infrastructure/Jobs/JobOptions.cs` — `Queues`, per-lane parallelism, validation
- `src/Infrastructure/Jobs/JobQueue.cs` — `IJobQueue` over `IMessageBus`
- `src/Infrastructure/Jobs/JobCurrentUser.cs` — `ICurrentUser` with no `HttpContext`
- `src/Infrastructure/Jobs/JobRegistration.cs` — lane→queue routing, listeners, error policies
- `src/Worker/AiFramework.Worker.csproj`, `Program.cs`, `appsettings.json`,
  `appsettings.Development.json`, `CLAUDE.md`, `Internal/Generated/`
- `k8s/base/worker.yaml`
- `tests/Worker.IntegrationTests/` — `WorkerFactory.cs`, `JobDeliveryTests.cs`,
  `WorkerCodegenTests.cs`
- `tests/Infrastructure.Tests/Jobs/` — `JobQueueTests.cs`, `JobRegistrationTests.cs`,
  `JobOptionsTests.cs`
- `tests/Api.IntegrationTests/Jobs/ApiPublishesOnlyTests.cs`
- `.claude/commands/job.md`
- `docs/adr/0016-jobs-in-a-worker-host.md` *(already written)*

**Modified:**
- `src/Infrastructure/EventPath/WolverineEventPath.cs` — host-role split (listen vs publish only)
- `src/Infrastructure/InfrastructureRegistration.cs` — `AddJobs()`
- `src/Api/Program.cs` — publish-only host role
- `Dockerfile.api` — a `worker` stage
- `AiFramework.slnx` — two new projects
- `.github/workflows/ci.yml` — worker codegen diff check
- `deploy/deploy.ps1` — build and load the worker image
- `scripts/dev.ps1` — optionally start the worker
- `.claude/hooks/hooks.config.json` **and** `.claude/hooks/dependency-rule.ps1` — `Worker` layer
- `CLAUDE.md` — a `## Jobs` section
- `docs/adr/0005-wolverine-for-the-event-path.md` — a pointer to 0016

---

## Task 1 — Settle transactional enqueue empirically ✅ DONE (2026-09-15)

> **RESULT: the fallback wins. `IJobQueue` does NOT use `IDbContextOutbox<T>`.**
>
> Measured by `tests/Api.IntegrationTests/EventPath/JobEnqueueAtomicitySpike.cs`, three cases,
> all passing:
>
> | Case | Result |
> |---|---|
> | Publish via `IDbContextOutbox<T>`, then plain `context.SaveChangesAsync()` | **Throws** — `NpgsqlRetryingExecutionStrategy does not support user-initiated transactions` |
> | The same, inside `CreateExecutionStrategy().ExecuteAsync(...)` | **Persists nothing** — `wolverine_outgoing_envelopes` unchanged |
> | `SaveChangesAndFlushMessagesAsync` inside the strategy | Persists and delivers, asynchronously via `wolverine_incoming_envelopes` |
>
> The decisive one was not anticipated: **resolving the outbox and publishing enrolls the
> `DbContext`, which opens a transaction**, so `UnitOfWork`'s plain `SaveChangesAsync` cannot
> commit at all. Adopting the EF outbox for jobs would mean wrapping *every* command's commit in
> an execution strategy **and** swapping in `SaveChangesAndFlushMessagesAsync` — a rewrite of the
> commit path to add a job framework. Refused.
>
> **Mechanism adopted:** a job that must not be lost is enqueued from an
> `IDomainEventHandler<T>`, reached through the existing `DomainEventsInterceptor` outbox — already
> transactional, already at-least-once, zero change to `UnitOfWork`. `IJobQueue` publishes through
> plain `IMessageBus`.
>
> **Consequence to carry into Task 2:** `IJobQueue`'s XML docs must state that a direct enqueue
> from a command handler is fire-and-forget and survives a failed command. Also useful: the
> Wolverine table names are `wolverine_outgoing_envelopes`, `wolverine_incoming_envelopes`,
> `wolverine_dead_letters`, `wolverine_nodes`, `wolverine_node_records`,
> `wolverine_node_assignments`, `wolverine_control_queue`, `wolverine_agent_restrictions`.
>
> **Spike retained, not deleted.** Cases A and B pin a trap that is otherwise invisible and would
> be rediscovered the next time someone reaches for `IDbContextOutbox`. Renamed in Task 3 to
> `JobEnqueueMechanismTests`, in the spirit of `WolverineLocalQueueDurabilityTests`.

### Original task, kept for the record

**This blocks the shape of `JobQueue` and must not be guessed.** The spec records the
uncertainty: whether envelopes published through `IDbContextOutbox<AiFrameworkDbContext>` survive
a commit made by the existing `IUnitOfWork.SaveChangesAsync` (rather than
`SaveChangesAndFlushMessagesAsync`), and if so how they are eventually delivered.

- [x] Write a throwaway test in `tests/Api.IntegrationTests/EventPath/` modelled on
      `WolverineOutboxAtomicityTests` — which already wraps its call in
      `context.Database.CreateExecutionStrategy().ExecuteAsync(...)`, required since
      `EnableRetryOnFailure` landed (ADR 0014).
- [x] Publish a message through `IDbContextOutbox<AiFrameworkDbContext>`, commit with plain
      `SaveChangesAsync`, and assert on **both**: (a) whether a row appears in the Wolverine
      outgoing envelope table in the `wolverine` schema, and (b) whether the message is ultimately
      delivered.
- [x] Assert the negative too: the same publish inside a transaction that **rolls back** must
      deliver nothing.
- [x] Record the finding as a comment at the top of `JobScheduler.cs`, in the style of
      `ObservabilityRegistration.BuildOtlpEndpoint`'s remarks — the empirical result, not the
      assumption.

**Decision gate:**
- **Envelopes written and later recovered** → `JobQueue` uses `IDbContextOutbox<T>`;
  `UnitOfWork` is untouched; document that in-request enqueue latency is bounded by the recovery
  sweep.
- **Envelopes not written** → fall back to the spec's recorded alternative: raise a domain event,
  and enqueue the job from an `IDomainEventHandler<T>`. Already transactional via
  `DomainEventsInterceptor`, no new mechanism, one extra hop.

- [x] Delete the throwaway test once the finding is recorded and a real test replaces it in
      Task 3.

**Verify:** `dotnet test tests/Api.IntegrationTests -c Debug` — the spike test passes and its
result is written down before any production code is added.

---

## Task 2 — Application contracts ✅ DONE (2026-09-15)

- [x] `src/Application/Abstractions/Jobs.cs`:
  - `public enum JobLane { Light, Heavy }`
  - `public interface IJob { static abstract JobLane Lane { get; } }`
  - `IJobQueue` with `EnqueueAsync<TJob>`, `ScheduleAsync<TJob>(…, DateTimeOffset, …)` and
    `ScheduleAsync<TJob>(…, TimeSpan, …)`, each constrained `where TJob : IJob`.
- [x] XML docs on each, stating: the lane is a static abstract so it reads with **no reflection**;
      a job carries the user it acts for because there is no `HttpContext` in the worker; and a
      job handler must not log its own outcome because `Behaviors.LoggedAsync` already does.
- [x] `src/Application/Jobs/RebuildOrderReport.cs` — the reference job: a `sealed record`
      implementing `IJob` with `Lane => JobLane.Heavy`, carrying `OwnerId`, plus its handler
      resolving `IQueryDispatcher` and doing something small and real.
- [x] No Wolverine reference anywhere in `src/Application`.

**Verify:** `dotnet build src/Application -c Debug` clean. Confirm the dependency-rule hook stays
silent — nothing here should trip it.

---

## Task 3 — Infrastructure: queue, options, caller, registration ✅ DONE (2026-09-15)

- [x] `JobOptions.cs` — `Queues` (comma-separated string, bound from `Jobs__Queues`),
      `Light.MaximumParallelMessages` (8), `Heavy.MaximumParallelMessages` (2). Validate that
      every name in `Queues` parses to a `JobLane`, so an unknown lane fails at startup rather
      than leaving a queue silently unconsumed — the same failure `OutboxOptions`' `WorkerCount >= 1`
      validation exists to prevent. Bind in each host's `Program.cs`, **not** in `AddJobs`, so
      `AddJobs` stays resolvable from a bare `ServiceCollection` in a unit test (the reason given
      for `CacheOptions` in `Infrastructure/CLAUDE.md`).
- [x] `JobScheduler.cs` — `IJobQueue` over `IMessageBus`, in the shape Task 1 settled. Lane→queue name
      mapping lives here and nowhere else; a second copy is how the two sides drift.
- [x] `JobCurrentUser.cs` — scoped `ICurrentUser` populated from the job message by a Wolverine
      middleware before the handler runs. Memoize like `Api/Auth/CurrentUser` does, and carry a
      comment explaining that this exists because `EvictAsync` and ADR 0007's ownership-in-the-query
      both read `ICurrentUser`, and the worker has no `HttpContext`.
- [x] `JobRegistration.cs`:
  - `MapJobs(WolverineOptions)` — one explicit `opts.PublishMessage<T>().ToPostgresqlQueue(...)`
    per job type, reading `T.Lane`. Explicit, greppable, mirroring `AddMessaging()`.
  - `ListenForJobs(WolverineOptions, JobOptions)` — `ListenToPostgresqlQueue(...)` per configured
    lane with that lane's `MaximumParallelMessages`.
  - Error policies: `.OnException<...>().ScheduleRetry(1m, 5m, 30m).Then.MoveToErrorQueue()`.
    **`ScheduleRetry`, not `RetryWithCooldown`** — a cooldown holds a listener slot for its whole
    duration, and on a two-slot lane one poisoned message would consume half the capacity. Put that
    reason in a comment; it is not obvious from the method names.
- [x] `InfrastructureRegistration.AddJobs()`, called from `AddInfrastructure`, registering
      `IJobQueue` and the job handlers' dependencies.
- [x] Tests in `tests/Infrastructure.Tests/Jobs/`:
  - `JobQueueTests` — lane→queue mapping; `ScheduleAsync` produces a **scheduled** envelope with
    the right time (assert on the envelope, never by waiting).
  - `JobRegistrationTests` — **completeness**: every `IJob` in the Application assembly has a
    registration. Model it on the existing `RegistrationCompletenessTests`.
  - `JobOptionsTests` — an unknown lane name in `Queues` fails validation.

**Verify:** `dotnet test tests/Infrastructure.Tests -c Debug`.

---

## Task 4 — Split the Wolverine host role, and prove the API listens to nothing ✅ DONE (2026-09-15)

- [x] `WolverineEventPath.AddWolverineEventPath` gains a host role — an enum or two entry points,
      `PublishOnly` and `ProcessesJobs`. Keep the existing `durable` and `usePreGeneratedCode`
      parameters and their documented reasoning intact.
- [x] The API passes `PublishOnly`: `MapJobs` runs, `ListenForJobs` does not.
- [x] Extend the existing `DisableConventionalDiscovery()` block with the job handler types the
      **worker** includes, and leave the API's list unchanged. Comment that this list is the
      enforcement point for "jobs never run in the API".
- [x] `tests/Api.IntegrationTests/Jobs/ApiPublishesOnlyTests.cs` — assert against
      `ServiceCapabilities.MessagingEndpoints` that no `jobs-*` queue appears as a **listener** on
      the API host. `WolverineLocalQueueDurabilityTests` already reaches endpoint modes this way;
      copy that route rather than inventing one.

**This is the test that makes the design a rule rather than an intention. Write it before the
worker exists, and watch it pass for the right reason.**

**Verify:** `dotnet test tests/Api.IntegrationTests -c Debug`. Then
`dotnet run --project src/Api -- codegen write` and commit the diff.

---

## Task 5 — The worker host ✅ DONE (2026-09-15)

- [x] `src/Worker/AiFramework.Worker.csproj` — `Microsoft.NET.Sdk.Web`, references `Application`
      and `Infrastructure`. Mirror `AiFramework.Api.csproj`'s Debug-only
      `WolverineFx.RuntimeCompilation` reference and its comment: Release ships without Roslyn and
      loads pre-generated adapters (33MB, measured — ADR 0005).
- [x] `src/Worker/Program.cs`:
  - The same `ConnectionStrings:Default` whitespace guard the API uses, and for the same reason —
    `GetConnectionString` returns `""`, not null, for an unset-but-present key.
  - `builder.AddObservability()` — unchanged, because this is a `WebApplicationBuilder`.
  - `builder.Services.AddInfrastructure(connectionString)`, `Configure<JobOptions>`,
    `Configure<CacheOptions>`.
  - `builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMinutes(5))` —
    .NET's 30s default would abandon in-flight heavy jobs long before Kubernetes' 300s grace period
    was willing to. Comment the pairing; the two numbers are meaningless apart.
  - `builder.Host.AddWolverineEventPath(..., typeof(Program).Assembly, role: ProcessesJobs)`.
  - `AddHealthChecks().AddDbContextCheck<AiFrameworkDbContext>()`.
  - `app.MapGet("/health", ...)` and `app.MapHealthChecks("/health/ready")` — **and no
    controllers**. Comment that these exist because `exec` probes need a shell and the chiseled
    image has none, the same fact `k8s/base/api.yaml` records for its `preStop` hook.
  - The same `args.Length == 0 ? RunAsync : RunJasperFxCommands(args)` split as the API, with the
    same reasoning, so `codegen write` is reachable without paying JasperFx's assembly scan on
    every ordinary start.
  - `public partial class Program { protected Program() { } }` for `WebApplicationFactory`, with
    the S1118 comment the API's carries.
- [x] `src/Worker/appsettings.json` — `Observability:ServiceName` = `aiframework-worker`,
      `Cache:Enabled` = `false`, `Jobs:Queues` = `light,heavy`, `Otlp:Enabled` = `false`.
- [x] `dotnet run --project src/Worker -- codegen write`; commit `src/Worker/Internal/Generated`.
- [x] Add both new projects to `AiFramework.slnx`.

**Verify:** `dotnet build -c Release` — Release is the configuration that proves the generated
tree loads. Then run the worker against the dev database and confirm it starts and answers
`/health`.

---

## Task 6 — Worker integration tests ✅ DONE (2026-09-15)

- [x] `tests/Worker.IntegrationTests/` with a `JasperFxTestEnvironment.cs` module initializer
      setting `JasperFxEnvironment.AutoStartHost` — without it the host never starts under
      `WebApplicationFactory`, which cost 23 of 24 tests in `Api.IntegrationTests` to discover.
- [x] `WorkerFactory.cs` over Testcontainers Postgres, mirroring `ApiFactory`.
- [x] `JobDeliveryTests` — a published job reaches its handler; a `Heavy` job lands on
      `jobs-heavy` and not on `jobs-light`; a handler that throws is retried and then
      dead-lettered, asserted on envelope state rather than by waiting.
- [x] `WorkerCodegenTests` — the worker's committed adapters are current, mirroring
      `WolverineCodegenTests`. **Verify it fails, naming the regenerate command, when
      `src/Worker/Internal/Generated` is deleted.** A staleness guard that has never been seen to
      fail is not a guard.
- [x] `JobCurrentUserTests` — a job carrying an owner resolves that owner through `ICurrentUser`
      inside the handler.

**Verify:** `dotnet test tests/Worker.IntegrationTests -c Debug` and again `-c Release`.

---

## Task 7 — Container and Kubernetes ✅ DONE (2026-09-15)

- [x] `Dockerfile.api` — a `worker` stage from `aspnet:10.0-noble-chiseled`, publishing
      `src/Worker/AiFramework.Worker.csproj` in **Release** (same reason the API's build stage
      says "Release, always"). Reuse the existing `build` stage; do not add a second file.
      Remember `COPY src/Worker/AiFramework.Worker.csproj src/Worker/` before the restore layer,
      or the cached restore misses it.
- [x] `k8s/base/worker.yaml` — Deployment only, **no Service** (nothing routes to it; `httpGet`
      probes address the pod directly). `replicas: 1`,
      `terminationGracePeriodSeconds: 300`, `requests: cpu 500m / memory 512Mi`,
      `limits: memory 1536Mi`, `readOnlyRootFilesystem: true`, `runAsNonRoot: true`,
      `capabilities.drop: ['ALL']`, `envFrom` the same `app-config` and `app-secrets`, plus
      `Jobs__Queues` and `Cache__Enabled: 'false'`. Startup/liveness on `/health`, readiness on
      `/health/ready` with an explicit `timeoutSeconds: 5` for the reason
      `k8s/base/api.yaml` gives — `CanConnectAsync` goes through the `EnableRetryOnFailure`
      execution strategy.
  - **No `preStop` sleep.** Comment why: the API needs one to leave Service endpoints before it
    stops accepting connections; nothing routes here, so what matters is Wolverine finishing
    in-flight messages, which is `HostOptions.ShutdownTimeout`.
- [x] `k8s/base/kustomization.yaml` — add `worker.yaml`.
- [x] `deploy/deploy.ps1` — build `aiframework-worker:local` and `kind load` it alongside the API
      image, and wait on the worker rollout.
- [x] `scripts/dev.ps1` — start the worker in its own window, so the compose loop exercises the
      same split the cluster does.

**Verify:** `./deploy/deploy.ps1`, then `kubectl -n <ns> get pods` shows the worker `Running`, and
`kubectl logs deploy/worker` shows Wolverine listening on both queues. Then `./deploy/e2e-k8s.ps1`
still passes — the worker must not disturb the existing gate.

---

## Task 8 — CI ✅ DONE (2026-09-15)

- [x] `.github/workflows/ci.yml`, `codegen` job — regenerate **both** trees and fail on either
      diff. The Api step already exists; add the worker step with the same
      `Wolverine__Durable=false` and placeholder connection string, and an `::error` message
      naming `dotnet run --project src/Worker -- codegen write`.
- [x] Confirm the `backend` matrix picks the new projects up without edits — it runs
      `dotnet build`/`dotnet test` over the solution, so adding them to `AiFramework.slnx` in
      Task 5 should be sufficient. **Check, do not assume.**

**Verify:** delete `src/Worker/Internal/Generated`, run the codegen job's commands locally, and
confirm they fail. Restore.

---

## Task 9 — Teach the hooks about the Worker layer ✅ DONE (2026-09-15)

- [x] `.claude/hooks/hooks.config.json` — add `"Worker"` to `layers`.
- [x] `.claude/hooks/dependency-rule.ps1` — add `'Worker' = @("$root.Api")` to `$banned`.
      **Both files, in the same change.** The script's own comment warns that a layer in the
      config with no `$banned` entry matches with an empty banned list, silently disarming that
      layer.
- [x] Add fixtures under `.claude/hooks/tests/fixtures/` — one `Worker` file importing
      `AiFramework.Api` (must be blocked), one importing `AiFramework.Infrastructure` (must pass).
- [x] Run `.claude/hooks/tests/run-hook-tests.ps1`.

**Verify:** the hook test suite passes, and the new blocked fixture genuinely exits 2.

---

## Task 10 — Teach the framework ✅ DONE (2026-09-15)

- [x] Root `CLAUDE.md` — a `## Jobs` section placed after `## Caching`, covering:
  - **Jobs run in the worker. The API listens to nothing.** State the rule in one sentence, name
    `ApiPublishesOnlyTests` as what enforces it, and the discovery list as where it is declared.
  - The two lanes, their queues, their parallelism, and that the host-to-queue mapping is
    `Jobs__Queues` — double underscores, same trap as `Cache__Enabled`.
  - **Three things that will cost you time**, in the house voice:
    1. **Two codegen trees now.** `codegen write` for `src/Api` *and* `src/Worker`; Debug stays
       green with either one stale, and only Release breaks, at startup.
    2. **A job cannot evict the API's cache.** L1-only `HybridCache`, and the worker is not behind
       the ingress affinity that makes eviction work between API pods at all. The worker runs with
       `Cache__Enabled=false` so this is explicit; the TTL is what bounds visibility.
    3. **There is no `HttpContext` in the worker.** The caller travels on the job message and a
       middleware populates `JobCurrentUser`. A job that presents no user reads nothing, because
       ADR 0007 puts ownership in the query.
  - `ScheduleRetry` over `RetryWithCooldown` on the heavy lane, and why.
  - Recurring jobs are self-rescheduling messages; no Quartz, no timer `IHostedService`.
  - `See ADR 0016.`
- [x] `src/Worker/CLAUDE.md` — what belongs here (composition, health endpoints, the generated
      tree) and what never does (feature logic, controllers, any `AiFramework.Api` namespace).
- [x] `.claude/commands/job.md` — `/job <JobName>`, mirroring `/feature`'s numbered shape:
      1. choose the lane and justify it; 2. the job record in `src/Application/Jobs/`;
      3. the handler; 4. register in `JobRegistration.cs` **and** in the worker's discovery list;
      5. tests at each layer; 6. **regenerate the worker's adapters and commit them**;
      7. `/verify` then `dotnet-reviewer`. End with `/feature`'s own closing rule: if a step is
      blocked, stop and say so rather than inventing a shape.
- [x] `docs/adr/0005-wolverine-for-the-event-path.md` — a line pointing at ADR 0016, noting jobs
      now ride the same runtime and that moving the outbox pumps to the worker is the open
      follow-on.

**Verify:** read the `## Jobs` section back cold and check it answers "where does this job run,
what do I regenerate, and what can it not do" without opening another file.

---

## Task 11 — Finish ✅ DONE (2026-09-15)

> **Verified, with the evidence:**
> - `dotnet build` clean in **both** Debug and Release.
> - **528 tests pass** across five projects: Domain 83, Application 120, Infrastructure 192,
>   Api.IntegrationTests 128, Worker.IntegrationTests 5. Counts confirmed via TRX where the
>   console summary disagreed under parallel execution.
> - **51/51 hook tests**, including the two new Worker fixtures in both directions.
> - Frontend untouched and still green: lint clean, build clean, 63 tests.
> - `openapi/AiFramework.Api.json` **unchanged** — nothing leaked into the API surface.
> - Both generated trees regenerate to no diff.
> - **The full stack deploys to kind**: worker 1/1 Running, 0 restarts, listening on
>   `postgresql://jobs_light/` and `postgresql://jobs_heavy/`; the API listening on **zero** job
>   queues, which is ADR 0016's rule holding in a real cluster rather than only in a test.
> - **`deploy/e2e-k8s.ps1`: 11/11 Playwright tests pass against the live cluster** with the
>   worker deployed — the gate that exercises durable Wolverine, caching on, two API replicas and
>   the real rate limit. Adding a third Wolverine node to the cluster disturbed none of it.
> - `dotnet-reviewer` run over the full diff; seven findings acted on, including a completeness
>   test that was documented but never written and two real bugs. See the `fix(jobs):` commit.

- [x] `/verify` — both stacks, both configurations.
- [x] `dotnet test -c Release` explicitly. Release is the one that proves both generated trees
      load, and Debug alone is not evidence — the reason CI builds both.
- [x] `./deploy/deploy.ps1` then `./deploy/e2e-k8s.ps1`.
- [x] Dispatch the `dotnet-reviewer` agent over the full diff. It carries the `Api → Infrastructure`
      "DI only" cell the hook cannot enforce, and `Worker → Infrastructure` is the same shape.
- [x] Confirm `openapi/AiFramework.Api.json` is **unchanged** — this plan touches no controller,
      DTO or `[ProducesResponseType]`, so a diff there means something leaked into the API surface.

---

## Open follow-ons, deliberately not in this plan

- **Move `OutboxPollerService`/`OutboxWorkerService` to the worker.** The obvious next step, and
  what finally retires ADR 0005's "two paths running side by side". Separate change: it touches
  `OutboxDeliveryTests` and the API's `terminationGracePeriodSeconds` reasoning.
- **KEDA and queue-depth autoscaling.** The `postgresql` scaler over a `COUNT` against the
  Wolverine queue table. The kind cluster stays at one replica.
- **RabbitMQ.** Four named triggers in ADR 0016; none of them hold yet.
