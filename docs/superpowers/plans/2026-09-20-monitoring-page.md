# Monitoring Page Implementation Plan

> **For agentic workers:** Steps use checkbox (`- [ ]`) syntax for tracking. Implement
> task-by-task, verifying each before starting the next. Each phase is independently shippable
> and must leave `/verify` and CI green.

**Goal:** One place an operator can answer "is it working, and if not, what broke" — job runs and
their outcomes, application traffic, sign-in history, outbox health — with enough drill-down to
reach the individual failure, and two actions to do something about it.

**Architecture:** Operational facts are **persisted in Postgres as first-class rows** and read
through the ordinary Clean Architecture query pipeline. Nothing on this page queries OpenSearch,
and nothing depends on `-WithObservability` being deployed: the page works identically under
`dotnet run`, under the e2e stack, and under kind. Each row carries its `TraceId`, so where a log
store *is* running the page is a deep-link away from the raw records rather than a reimplementation
of them.

**Tech Stack:** .NET 10 (net10.0), `LangVersion` 14.0, EF Core + Npgsql 10.0.3, Wolverine 6.33.0,
Quartz 4.1.0, React 19.3 + TanStack Query 5, Vite 8, xUnit + FluentAssertions + NSubstitute,
Testcontainers, Playwright.

**ADRs:** [`0020-an-administrator-role.md`](../../adr/0020-an-administrator-role.md),
[`0021-operational-telemetry-in-postgres.md`](../../adr/0021-operational-telemetry-in-postgres.md)

**Anchored in ADR 0017**, whose Context already names this work:

> "The roadmap now expects many scheduled jobs and a monitoring page that runs jobs, shows their
> runs and logs, and shows the health of external integrations. […] admin authorization, run
> history, integration health checks, and the monitoring page itself are separate pieces built
> later, on top of this one."

ADR 0017 also already decided **where run history comes from**: not Quartz, which "would record
only 'fired,' never the job's outcome," but the Wolverine layer, "built once […] and see every
run" — whether the trigger is a schedule, a domain event, or a button. This plan implements that.

---

## Decisions taken

| Question | Decision |
|---|---|
| Who can see it | A real **Admin role** on `User`, with an authorization policy. Not a config gate. |
| Where data comes from | **Postgres**, new operational tables. Not OpenSearch, not Prometheus. |
| Traffic means | RED metrics (rate/latency/errors), per-handler command/query stats, and who's online. |
| Page's job | All four: at-a-glance health, drill-down debugging, audit, and operator actions. |
| Metric aggregation | **Per-pod in-memory buckets flushed to Postgres every minute**, summed across pods. |
| Who's online | **`LastSeenAt`, written at most once per user per minute** from the session-validation path. |
| Operator actions | **Retry a failed/dead-lettered job** and **trigger a scheduled job now**. |
| Retention | Sign-in events and job runs **30 days** with IP and user-agent captured; traffic rollups **7 days**. |
| First admin | **Config-driven promotion** (`Admin__Usernames`), reconciled idempotently at startup. |
| UI shape | `/monitoring` overview, with drill-down sub-pages per area. |
| Refresh | **TanStack Query polling**, interval per panel. No SignalR. |
| Phasing | Four phases, admin role first. |

### Deliberately out of scope

- **Replaying a dead outbox row.** Left out by choice. Worth revisiting: `OutboxMessage` already
  has a `Status` state machine with a `Dead` member and a `LastError`, so this is the cheapest of
  the four actions considered and the only one needing no new read path.
- **Unlocking a locked-out account.** Per ADR 0011 it must rotate the security stamp, which signs
  that user out everywhere — a user-management concern rather than a monitoring one.
- **Log and trace search in-app.** OpenSearch Dashboards exists for that under
  `deploy.ps1 -WithObservability`. The page deep-links by `TraceId` instead.
- **Integration health checks** (ADR 0017's fourth roadmap piece). `ExchangeRateClient` is the only
  outbound integration; a later phase.

---

## Global Constraints

- **Warnings are errors** — compiler, analyzers and build. No suppression without a narrow
  `#pragma warning disable`/`restore` pair carrying a justification comment above it.
- **Nullable is enabled. Never `catch (Exception)`** — CA1031 is an error outside the outbox's
  file-scoped `.editorconfig` exemption. The job-run recorder is **not** a new exemption: it runs
  as Wolverine middleware, which receives the failure.
- **The dependency rule is hook-enforced.** No Wolverine, Quartz, EF or `HttpContext` type may
  appear in `src/Application` — every one of them reaches the page through a port.
- **`required` in Domain, never `[Required]`.** DataAnnotations only on Api DTOs.
- **Never hand-edit an applied migration.** Four new ones here; add, never amend.
- **Config keys use double underscores** — `Admin__Usernames__0`, `Monitoring__Retention__Days`.
  A single underscore binds nothing and warns nothing.
- **After changing any Wolverine handler or middleware, regenerate BOTH trees**
  (`dotnet run --project src/Api -- codegen write` and the same for `src/Worker`) and commit the
  result. Phase 2 touches worker middleware, so this is not optional there.
- **After changing a controller, a DTO or a `[ProducesResponseType]`, regenerate the contract**
  (`openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts`). Phases 1–4 all do.
- **Nothing on this page is `ICacheable`.** Monitoring data is the current state of the system; a
  thirty-second stale cache is exactly wrong here, and `CacheScope` would scope global operational
  data per-caller anyway. Same posture as the auth path and the notification feed.
- **No `Thread.Sleep`, no `Task.Delay`, no retry-until-timeout in tests.** Bucket boundaries and
  retention windows are asserted through `IClock`, never by waiting.
- Run `dotnet test <project>` one project at a time — two paths in one invocation fails MSB1008.
- Branch: `claude/monitoring-page-planning-drirkw`.

---

## Phase 1 — The Admin role

Nothing else can be built honestly until there is someone authorized to see it. Today every
authenticated user is equal: `[Authorize(Policy` appears nowhere in the solution.

### Data model

`User` gains `UserRole Role { get; private set; }` — `enum UserRole { Member, Admin }` in
`src/Domain/Users`, defaulting to `Member`.

### Tasks

- [x] **1.1** Add `UserRole` and `User.Role` to `src/Domain/Users/User.cs`, with a
      `ChangeRole(UserRole)` method that **does NOT rotate `SecurityStamp`** — see ADR 0020. The
      role is read from the database on every request rather than carried in the cookie, so there
      is no issued credential holding stale authority to invalidate, and rotating would sign a user
      out of a session they remain entitled to hold. **Correct `User.SecurityStamp`'s summary in
      the same commit**: its "(from plan 2) a permission change" parenthetical is superseded, and
      the list of rotation triggers stays at three. Unit tests in `tests/Domain.Tests`, including
      one asserting a role change leaves the stamp untouched.
- [x] **1.2** EF configuration + migration `AddUserRole`. Store the enum as a **string**, not an
      int: `NotificationKind` and `OrderStatus` already cross the wire as names for exactly the
      reason that reordering members silently changes what stored values mean.
- [x] **1.3** `AdminOptions` bound from `Admin:Usernames`, with validation. Empty is legal and
      means "no administrators", which must not fail startup.
- [x] **1.4** `AdminReconciler` — an idempotent single-statement `UPDATE` promoting listed
      usernames and demoting anyone holding `Admin` who is no longer listed, run by a hosted
      service at API startup. Idempotent because **both API replicas run it**; concurrent identical
      updates are harmless, and the `UPDATE` is conditional on the role actually differing, so an
      unchanged list writes nothing at all. Demotion needs no stamp rotation: the role is read per
      request, so it takes effect on the demoted user's next one. Match on `UsernameNormalized`,
      never `Username` — `User.Normalize` is the only lookup key.
- [x] **1.5** Authorization policy, fed by a **per-request read**. Widen `ISessionValidator` from
      `IsStampCurrentAsync` returning `bool` to a validation returning the verdict **and** the
      role: `SessionValidator` already issues one projected, uncached, primary-key read per
      authenticated request, so adding `Role` to that `Select` is the same row on the same index
      seek. `OnValidatePrincipal` then attaches the role to the principal for the current request
      with `ReplacePrincipal`, **without** renewing the cookie. Register a `"Monitoring"` policy
      requiring `UserRole.Admin`. **The role is never written into the cookie** — that is the whole
      point, and it is what makes a demotion take effect immediately. Every call site and test
      double of `ISessionValidator` moves with the signature.
- [x] **1.6** Extend `GET /api/auth/me`'s `SessionView` with the role, so the SPA can decide
      whether to render the nav entry. Regenerate the contract.
- [x] **1.7** Frontend: a `RequireRole` route element mirroring `RequireAuth`, the `/monitoring`
      route behind it, and a nav entry rendered only for admins. The page itself is a stub this
      phase.
- [x] **1.8** Integration tests: a `Member` gets **403** (not 404, not 401) on a monitoring
      endpoint; an `Admin` gets 200; an anonymous caller gets 401; and **a demotion takes effect on
      the next request of an already-signed-in admin, with no re-authentication** — the test that
      proves the role is not cookie-borne. Vitest coverage for `RequireRole` and for the nav entry
      being absent for a member.

### What changed during implementation

Three departures from the tasks above, each forced by something that only showed up once the code
ran. Recorded here so this plan still reads correctly against the code.

- **`AdminOptions`, its validator and `AdminReconciler` live in `src/Infrastructure/Security`,
  not in `src/Api/Auth`.** Surviving an absent database means catching what an absent database
  throws, and that turned out to be **two** types: `DbException` for a refusal the provider
  reports directly, and EF's `RetryLimitExceededException` once `EnableRetryOnFailure` has
  exhausted its attempts on a transient one. The second is an EF type and `src/Api` contains no
  EF reference at all — so the reconciler moved to the layer where EF is already at home, and Api
  composes it through `AddAdministratorRoles()` with both types staying `internal`.
- **A new configuration switch, `Admin__ReconcileOnStart`.** OpenAPI document generation runs the
  whole application against a connection string that is never opened; the reconcile opened it and
  failed the `contract` build with an `ObjectDisposedException` naming nothing useful. It is now
  off in CI's contract job and in the documented regeneration command, exactly parallel to
  `Wolverine__Durable=false` and for the identical reason. `codegen write` does NOT need it — a
  JasperFx command never starts hosted services, confirmed by running it both ways.
- **`HealthTests` sets that switch too.** It stands up a host with a placeholder connection string
  precisely to prove `/health` needs no database, and its own comment records the Wolverine spike
  breaking it the same way once before. It is the canary for this class of change, and it caught
  this one.

The reconcile itself is entity-based — load the bounded candidate set, call `User.ChangeRole`,
let the unit of work commit — rather than the single set-based `UPDATE` task 1.4 first described.
`ChangeRole` reporting whether it actually moved is what keeps an unchanged list from dirtying
anything, so the "writes nothing at all" property survives the change.

### Traps

- **This deploy signs nobody out**, in pointed contrast with ADR 0011's own. The role is not a
  claim, so a cookie minted before this change is missing nothing — the role is supplied fresh from
  the database on every request. Existing rows default to `Member`, the safe direction. Assert it,
  do not assume it.
- **Configuration is the authority, so a hand-written SQL promotion is reverted at the next API
  start.** That is the declarative property working correctly, and it is exactly the kind of silent
  reversal that costs an afternoon. It belongs in the root `CLAUDE.md` when this phase lands.
- **Hiding the nav entry is not authorization.** The server check is the control; the nav entry is
  cosmetics. Both, and the test suite must cover the server side independently.
- **`AdminReconciler` runs before the database may be ready.** It must tolerate a startup where
  migrations have not been applied — fail the reconcile, not the host, and log at `Warning`.

---

## Phase 2 — Job runs, dead letters, and the two actions

### Data model

`job_runs`, a new EF-mapped table:

| Column | Notes |
|---|---|
| `Id` | The Wolverine envelope id — the natural key, and what makes a retry correlate to its original |
| `JobName` | The message type's name, e.g. `RebuildOrderReport` |
| `Lane` | `Light` / `Heavy`, stored as a string |
| `Status` | `Running`, `Succeeded`, `Failed`, `DeadLettered` |
| `Attempt` | Wolverine's attempt number, so retries are visible as retries |
| `StartedAt`, `CompletedAt`, `DurationMs` | |
| `OwnerId` | Null for a job that is not `IUserScopedJob` |
| `Error` | The exception message and type on failure; null otherwise |
| `TraceId` | The deep link out to the log store |
| `InstanceId` | `HOSTNAME`, matching what `ObservabilityRegistration` already uses for `serviceInstanceId` |

Indexed on `(StartedAt DESC)` and `(Status, StartedAt DESC)` — the two orders the page reads in.

### Tasks

- [x] **2.0 — SPIKE, done. Result below.** Establish how Wolverine 6.33 surfaces a handler's *outcome*
      to middleware. `JobUserMiddleware` proves the `Before(Envelope, …)` shape works; what is
      **not** yet established in this repo is whether a `Finally` method can receive the thrown
      exception, and whether it runs before or after the `OnAnyException` policy decides to retry
      or dead-letter. Three candidate mechanisms, in order of preference:
      1. `Finally(Envelope, Exception?)` on the middleware, if Wolverine binds it.
      2. A failure listener / observer registered on `WolverineOptions`, recording the terminal
         outcome separately from the middleware that records the start.
      3. Recording `Failed` from the error-policy continuation itself.
      **Result: mechanism 1 works, but only in a shape the plan did not anticipate, and the
      obvious spelling of it is a silent production bug.** Measured against Wolverine 6.33.0 by
      generating each candidate shape and reading the emitted adapter; written up in full in
      `src/Worker/CLAUDE.md`. In short:

      - `After` runs inside the try, immediately after the handler, so it fires on success only.
      - `OnException` binds the exception **as its first parameter** and can take DI services
        alongside it. `OnException(Envelope, Exception)` is silently dropped — no warning, green
        build, method simply absent from the adapter. The async spelling is fine:
        `OnExceptionAsync(Exception, …)` binds and is awaited, as do `BeforeAsync`/`AfterAsync`,
        so the recorder needs no sync-over-async to reach the database.
      - **The generated catch block emits no rethrow.** An `OnException` method therefore swallows
        the failure and silently disables this host's whole retry and dead-letter policy. The
        middleware must rethrow with `ExceptionDispatchInfo.Capture(exception).Throw()` —
        `throw exception;` is a CA2200 error here and erases the stack.
      - `Finally` runs *before* `OnException`, and `Envelope` carries no failure state, so neither
        is usable as the place to write the outcome.

      **So the recorder is `Before` + `After` + `OnException`, not `Before` + `Finally`.** Task 2.2
      changes accordingly, and 2.11 gains a test that a failing job still reaches the dead-letter
      queue — the regression this shape invites.
- [ ] **2.1** `IJobRunRecorder` port in `src/Application/Abstractions`, with the Infrastructure
      adapter writing via **immediate SQL**, following `OrderAuditWriter`'s precedent — not a
      tracked entity. Two independent reasons: the run record must survive the handler's own
      transaction rolling back, and a middleware that calls `SaveChangesAsync` would commit the
      handler's half-finished work with it.
- [ ] **2.2** `JobRunMiddleware`, registered beside `JobUserMiddleware` in
      `src/Infrastructure/Jobs/JobRegistration.cs`, as `Before` + `After` + `OnException` per the
      spike. `Before` and `After` take **`Envelope`**, never `IJob` — CLAUDE.md's "JasperFx will
      not upcast a concrete message to an interface for a middleware parameter" is exactly this
      situation, and `JobUserMiddleware` already carries the workaround. `OnException` takes the
      **exception first**, then its DI services, and **must end with
      `ExceptionDispatchInfo.Capture(exception).Throw()`** or it swallows every job failure.
- [ ] **2.3** Regenerate **both** codegen trees and commit. A middleware change is precisely the
      class of change that leaves Debug green and breaks Release at startup.
- [ ] **2.4** Dead-letter read adapter. Wolverine's envelope tables live in the **`wolverine`
      schema**, which Wolverine itself owns and migrates — read them with raw SQL through an
      Infrastructure adapter, and **never** map them as EF entities or touch them in a migration.
      `WolverineEventPath.EnvelopeSchema` is the constant.
- [ ] **2.5** Queries: `GetJobRuns` (paged, filterable by status/name/date), `GetJobRun` (one run
      with its full error), `GetDeadLetters`, `GetJobHealth` (counts by status over a window).
      None `ICacheable`.
- [ ] **2.6** **Action: retry a dead-lettered job.** A command marking the stored envelope
      replayable. The worker picks it up from the shared Postgres message store on its own — the
      API never talks to the worker.
- [ ] **2.7** **Action: trigger a scheduled job now.** Enqueue through `IJobScheduler`, which the
      API can already do: it registers *routing* for every lane and publishing is how a job starts.
      Only parameterless jobs are eligible, which `JobDescriptor.Scheduled<TJob>`'s `new()`
      constraint already guarantees; the endpoint must reject anything else by name lookup against
      `JobRegistration` rather than by reflection.
- [ ] **2.8** `MonitoringController` with the read endpoints and the two actions. `[Authorize(Policy
      = "Monitoring")]` on the controller. Regenerate the contract.
- [ ] **2.9** Frontend `/monitoring/jobs`: a filterable run table, a run detail panel, a dead-letter
      list, and the two action buttons with confirmation. Polling at ~10s.
- [ ] **2.10** Retention: extend `PruneProcessedOutbox`'s sibling — a scheduled job pruning
      `job_runs` past 30 days, declared with `JobDescriptor.Scheduled<T>` exactly as that one is.
- [ ] **2.11** Tests: middleware records a success, a failure and a retry as separate attempts with
      one shared `TraceId`; the recorder survives a rolled-back handler transaction; a retry action
      re-delivers; `JobRegistrationTests` still passes.

### Traps

- **Both actions are writes from the API into the worker's world, and that is fine** precisely
  because the transport is Postgres. This is a property worth stating in the ADR: it is what makes
  the page possible without exposing the worker through the ingress. If the transport ever becomes
  RabbitMQ (deferred, with named triggers, in CLAUDE.md), re-examine both.
- **A job that dead-letters has already been retried.** The page must show attempts as a group, or
  every transient blip reads as three separate failures.
- **The worker runs with `Cache__Enabled=false`** and is not behind the ingress affinity. Nothing
  the recorder writes can evict anything the API cached — which is another reason none of these
  queries may be `ICacheable`.

---

## Phase 3 — Sign-in audit and who's online

### Data model

`sign_in_events`:

| Column | Notes |
|---|---|
| `Id`, `At` | |
| `UserId` | **Nullable** — an attempt against a username that does not exist has no user |
| `UsernameAttempted` | What was typed, so unknown-user attempts are still investigable |
| `Outcome` | `Succeeded`, `BadCredentials`, `LockedOut`, `UnknownUser`, `SignedOutEverywhere` |
| `IpAddress`, `UserAgent` | Captured, per the retention decision |
| `TraceId` | |

`User` gains `LastSeenAt`.

### Tasks

- [ ] **3.1** `IClientContext` port exposing caller IP and user-agent, registered **per host** —
      the API binds it to `HttpContext`, and it is simply absent in the worker. This mirrors
      `ICurrentUser` exactly, including its trap: register it in `AddInfrastructure` and the
      API's registration is replaced, because the last registration wins.
- [ ] **3.2** `ISignInAudit` port + adapter writing via immediate SQL. Called from the sign-in and
      sign-out-everywhere paths.
- [ ] **3.3** Record every outcome, including `UnknownUser`. **The response must not change** — a
      differing message or timing between "no such user" and "wrong password" is a username
      oracle. Assert the responses are identical in a test.
- [ ] **3.4** Migration `AddSignInEventsAndLastSeen`.
- [ ] **3.5** `LastSeenAt` write in `Program.cs`'s `OnValidatePrincipal`, throttled: a **single
      conditional `UPDATE`** (`WHERE "LastSeenAt" IS NULL OR "LastSeenAt" < @cutoff`), never
      read-modify-write. That callback already costs one uncached read on **every authenticated
      request**; this must add at most one write per user per minute, and must never fail the
      request if it fails.
- [ ] **3.6** Queries: `GetSignInEvents` (paged, filter by outcome/user/date), `GetLockedOutUsers`,
      `GetActiveUsers` (seen within N minutes, N configurable).
- [ ] **3.7** Frontend `/monitoring/logins`: the event table with outcome filters, a locked-out
      panel, and an online-now count. Polling at ~30s.
- [ ] **3.8** Retention: a scheduled pruning job at 30 days, same shape as Phase 2's.
- [ ] **3.9** Tests: an unknown-username attempt is recorded and the response is byte-identical to
      a wrong-password one; a lockout is recorded; `LastSeenAt` is not written twice inside the
      throttle window (asserted through `IClock`, not by waiting).

### Traps

- **Recording IP and user-agent makes this table personal data.** The 30-day pruner is part of the
  feature, not a follow-up, and the retention window belongs in configuration with the default
  committed.
- **`X-Forwarded-For` only means anything behind the ingress.** `ForwardedHeaders__Enabled` is
  already a config key in the k8s overlay; without it every IP recorded in the cluster is the
  ingress pod's. Verify against the deployed cluster, not against `dotnet run`.
- **Do not cache anything here.** ADR 0008's lockout state must be read every time, and
  `GetUser` must stay non-`ICacheable` — that rule is permanent.

---

## Phase 4 — Traffic

### Data model

`traffic_buckets`, one row per (bucket, kind, name, instance):

| Column | Notes |
|---|---|
| `BucketStart` | Truncated to the minute, UTC |
| `Kind` | `Http`, `Command`, `Query` |
| `Name` | The **route template** for HTTP (never the raw path — cardinality), the request type name otherwise |
| `Succeeded`, `Failed`, `Faulted` | Counts, matching `LoggedAsync`'s own three outcomes |
| `DurationMsTotal` | For the mean |
| `Bucket0..BucketN` | Fixed latency histogram buckets — 5/10/25/50/100/250/500/1000/2500/5000ms |
| `InstanceId` | `HOSTNAME`. **The page sums across instances**; this is what makes two API replicas correct rather than showing whichever pod answered |

### Tasks

- [ ] **4.1** `ITrafficRecorder` — a singleton accumulating counts in memory, and a hosted service
      flushing completed buckets to Postgres once a minute. One upsert per (bucket, kind, name),
      not one row per request.
- [ ] **4.2** Hook `Behaviors.LoggedAsync`, which **already computes the name, the outcome and the
      elapsed milliseconds** for every command and query — the recorder needs no new timing. Resolve
      it with `sp.GetService`, not `GetRequiredService`, exactly as that method already resolves
      `ICurrentUser`: the outbox pumps dispatch with no recorder registered, and that is normal
      rather than a wiring error.
- [ ] **4.3** An ASP.NET Core middleware recording HTTP RED into the same recorder, keyed on the
      **endpoint's route template**. A raw path key would make one row per order id.
- [ ] **4.4** Migration `AddTrafficBuckets`, indexed on `(BucketStart DESC)`.
- [ ] **4.5** Queries: `GetTrafficSummary` (rate, error rate, p50/p95/p99 over a window) and
      `GetTrafficSeries` (per-bucket, for the chart). Percentiles are interpolated from the summed
      histogram buckets — **a mean is not a substitute**, and summing per-pod means across pods
      would be wrong regardless.
- [ ] **4.6** `/monitoring/traffic`: a rate-and-errors chart, a latency chart, and a per-endpoint /
      per-handler table sortable by volume, error rate and p95.
- [ ] **4.7** The `/monitoring` overview itself: health tiles (API and worker `/health/ready`,
      outbox depth, jobs failed in the last hour, sign-in failures in the last hour, requests/min,
      online now), each linking to its sub-page.
- [ ] **4.8** Retention: traffic buckets pruned at 7 days.
- [ ] **4.9** Playwright coverage for the overview and one drill-down, plus an e2e assertion that a
      member is refused the route.

### Traps

- **The bucket flush must be idempotent across a restarted pod.** Upsert on
  `(BucketStart, Kind, Name, InstanceId)`; a pod that dies mid-bucket loses at most a minute of its
  own counts, which is the accepted cost of not writing a row per request.
- **Clock skew between pods is real.** Truncate to the minute using `IClock` and accept that a
  bucket boundary is approximate. Do not build anything that needs sub-minute precision from this.
- **The worker must flush too**, or job-triggered commands and queries are invisible. Same recorder,
  same hosted service, different `InstanceId`.

### Open decision for this phase

**No chart library is installed.** `frontend/package.json` carries five runtime dependencies
(`@microsoft/signalr`, TanStack Query, React, React DOM, React Router) and nothing for charts.
Two options, to settle before 4.6:

1. **Hand-rolled inline SVG** for sparklines and a simple time series. No new dependency, fits the
   repo's minimal posture, and is genuinely sufficient for a line chart and a bar chart. More code
   to own, and axes/tooltips get tedious.
2. **Add Recharts (or similar).** Far less code, better interaction out of the box, and a new
   runtime dependency plus bundle weight on a page most users will never open — which argues for
   lazy-loading the monitoring routes regardless of which option wins.

**Recommendation: option 1 for Phase 4's two charts**, revisited if the page grows a third chart
type. Either way the `dataviz` skill governs the palette and chart-form choices.

---

## Verification

Each phase ends green on all of:

```bash
/verify                                                   # both stacks, plus codegen and contract diffs
dotnet run --project src/Api -- codegen write             # Phase 2 especially
dotnet run --project src/Worker -- codegen write
```

And CI's five jobs — `backend` (Debug **and** Release), `codegen`, `contract`, `frontend`, `e2e`.
Release is not optional evidence here: Phase 2 changes Wolverine middleware, and a stale generated
tree leaves Debug green and breaks Release at startup.

## Risks

| Risk | Mitigation |
|---|---|
| Task 2.0's spike finds no clean outcome hook in Wolverine | Fall back to the failure-listener or error-policy mechanisms; worst case, record only start/success in middleware and terminal failures from the dead-letter table |
| Write volume on the hot path (`LastSeenAt`, traffic flush) | Both are bounded by construction — one write per user per minute, one upsert per bucket per name per pod — and both must be measured, not assumed |
| The page becomes a second, worse log viewer | It deliberately stores *operational facts*, not log lines, and deep-links to OpenSearch for the rest |
| Four migrations across four phases | Each phase adds its own; never amend an applied one |
| Admin role interacts with session invalidation | The role is read per request and never carried in the cookie, so a role change needs no rotation at all — ADR 0020. Tested by 1.8's demotion case |
