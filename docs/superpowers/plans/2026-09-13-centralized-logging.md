# Centralized Logging Implementation Plan

**Status:** Planning. Nothing in `src/` has been changed by this document.

**Goal:** Every command, query, domain event and HTTP request produces a structured, correlated
log record, with no per-handler boilerplate, exported over OTLP to a store chosen by
configuration rather than by code.

**Architecture:** Plain `Microsoft.Extensions.Logging` with `[LoggerMessage]` source-generated
partials as the *writing* API, OpenTelemetry as the *export* pipeline, and a new pipeline
behavior in `Infrastructure/Messaging` as the thing that makes it automatic. The behavior sits
beside `ValidateAsync`, `CommitAsync`, `CachedAsync` and `EvictAsync` in exactly the shape ADR
0009 already established, so no handler learns that logging exists.

**Tech Stack:** .NET 10 (`net10.0`), OpenTelemetry 1.18.0, `Npgsql.OpenTelemetry` 10.0.3, Seq
(dev), OpenSearch behind an OpenTelemetry Collector (kind cluster). xUnit + FluentAssertions +
NSubstitute.

---

## Why not Serilog, and why not a base class

Both were on the table. Both are answered by facts already in this repository, so the reasoning
is recorded here rather than left to be rediscovered.

### Serilog

Serilog's genuine advantages over `Microsoft.Extensions.Logging` are its enrichment model
(`LogContext.PushProperty`), its sink ecosystem, and `appsettings`-driven sink configuration.
Set against this codebase:

- **The sink ecosystem is the thing being avoided.** A Serilog sink writing straight to
  OpenSearch couples `AiFramework.Api.dll` to the log store: changing stores becomes a package
  change, a rebuild and a redeploy. Exporting OTLP makes it a config-map edit.
- **A file sink is not available anyway.** `k8s/base/api.yaml` sets
  `readOnlyRootFilesystem: true`, deliberately and with a comment explaining that nothing in the
  running API writes to disk. Rolling files — Serilog's most common reason for existing — would
  need a volume and would give that guarantee up.
- **`Log.Logger` is a static singleton bootstrap.** This repo has no ambient statics on the
  request path; `IClock` exists precisely so that `DateTimeOffset.UtcNow` is never called
  directly.
- **It would not reduce the amount of code written.** CA1848 applies to `ILogger` regardless of
  who implements it (see below), so the `[LoggerMessage]` partials get written either way.

Serilog remains a reasonable choice for a different codebase. It is not the cheaper one here.

### A logging base class

The request was for logging that is seamless, "if putting the logging into a base class then
it's also good". A base class is the wrong mechanism *in this repository*, for four reasons:

1. **CA1848 is an error here.** `src/Api/GlobalExceptionHandler.cs:53` carries the comment
   *"CA1848: log the message via the source-generated LoggerMessage delegate rather than calling
   `ILogger.LogError` directly"*, and both it and `OutboxHostedServices.cs` are `partial` classes
   with `[LoggerMessage]` methods — the codebase has already paid this tax twice.
   `AnalysisMode=Recommended` plus `CodeAnalysisTreatWarningsAsErrors` in `Directory.Build.props`
   is what makes it a build failure. A base-class convenience method
   (`protected void LogInfo(string message)`) trips CA1848 at its own call site, or CA2254 if the
   template is not a constant. Either needs a `#pragma` pair with a justification comment, on a
   path used by every handler in the solution. That is the opposite of what the non-negotiables
   in `CLAUDE.md` ask for.
2. **Every handler is `sealed`, with a primary constructor.** There is no inheritance anywhere in
   `src/Application`. An abstract base would be the first, and each of the thirteen registered
   handlers would need its declaration changed to gain anything.
3. **A base class does not make logging automatic — it makes it shorter.** You still write a call
   in every handler, and the handler that forgets is still silent.
4. **The seam already exists and is already used four times.** `MessagingRegistration.AddCommand`
   wraps every command in `ValidateAsync` → handler → `CommitAsync` → `EvictAsync`;
   `AddQuery` wraps every query in `CachedAsync`. One more wrapper there gives automatic,
   uniform logging for **every command and query in the solution with zero handler edits** —
   which is the outcome a base class was reaching for, expressed in the idiom this codebase
   already speaks.

**So: a pipeline behavior, not a base class.** Handlers stay exactly as they are. For the rare
case where a handler wants to say something only it knows, it injects `ILogger<T>` directly
(Task 3 covers whether that is allowed in `Application`).

---

## What exists today

Worth stating plainly, because it is less than it looks:

| | |
|---|---|
| Production files that log at all | **2** — `GlobalExceptionHandler` (1 message), `OutboxHostedServices` (3 messages) |
| Request logging | none (`appsettings.json` sets `Microsoft.AspNetCore: Warning`, which suppresses the framework's own) |
| Correlation id | `traceId` is written into every `ProblemDetails` in three places, but see below |
| OpenTelemetry | none. No `ActivitySource`, no exporter, no collector |
| Log storage | stdout, unstructured, unretained |

**The `traceId` in `ProblemDetails` is currently close to useless, and this plan is what fixes
it.** `GlobalExceptionHandler.cs:41`, `ResultExtensions.cs:32` and `Program.cs:174` all write
`Activity.Current?.Id ?? httpContext.TraceIdentifier`. With no OpenTelemetry listener registered,
`Activity.Current` is null, so every one of them falls through to `TraceIdentifier` — a
per-connection string that appears in no log record anywhere. An operator handed a `traceId` from
a 500 today has nothing to search. Registering a tracer provider makes `Activity.Current` real,
stamps `TraceId`/`SpanId` onto every log record emitted during that request, and turns those
three existing lines into working correlation without editing any of them.

### Verified: no test pins the `traceId` format

This change does alter the *shape* of the value (from `0HN7...` to W3C
`00-<32 hex>-<16 hex>-01`). Both places that touch it are safe:

- `Api.IntegrationTests/Orders/ProblemDetailsContractTests.cs:41` asserts presence and
  non-emptiness only.
- `Api.IntegrationTests/Auth/AuthEndpointTests.cs:332` (`StripTraceIdAsync`) reads the value out
  of the body and string-replaces it, so it is format-agnostic by construction.

No change to `openapi/AiFramework.Api.json` follows from this plan — no controller, DTO or
`[ProducesResponseType]` is touched — so CI's `contract` job needs no regeneration.

---

## Global Constraints

Carried from `CLAUDE.md`; repeated because each one bites this plan specifically.

- **Warnings are errors** — compiler, analyzers, build. Every log call site must be a
  `[LoggerMessage]` partial (CA1848). No suppression without a `#pragma` pair and a
  justification comment.
- **Never `catch (Exception)`.** CA1031 is an error outside the outbox's file-scoped exemption.
  **This rules out a `try/catch` in the logging behavior.** See Task 2 — the behavior uses
  `try/finally` with no `catch`, and thrown exceptions stay the job of `GlobalExceptionHandler`,
  which already logs them at `Error` with the exception object attached. Logging them in the
  behavior as well would both violate CA1031 and double-report.
- **The dependency rule is hook-enforced** by `.claude/hooks/dependency-rule.ps1`. Its banned
  list for `Application` is `AiFramework.Infrastructure`, `AiFramework.Api`,
  `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`. `Microsoft.Extensions.Logging` is
  **not** on it — so whether `Application` may log is a design decision, not a blocked one.
  Task 3 decides it explicitly.
- **Never hand-edit an applied EF migration.** Task 5 adds a column and therefore a new
  migration.
- **No `Thread.Sleep` in tests.** Nothing in this plan waits on an exporter.
- **Config keys use double underscores in k8s.** `Observability__Otlp__Endpoint`, never
  `Observability_Otlp_Endpoint` — a single underscore binds nothing and warns nothing.
- **Verify with:** `dotnet build --nologo --verbosity quiet` then
  `dotnet test --nologo --verbosity quiet`.

---

## Target shape

```
                    ┌──────────────────────────────────────────┐
   Api ────────────▶│  ILogger<T>  ([LoggerMessage] partials)   │
   Infrastructure ─▶│                                          │
   Application ────▶│  + Activity (TraceId / SpanId stamped on  │
                    │    every record automatically)           │
                    └────────────────────┬─────────────────────┘
                                         │ OTLP/HTTP
                    ┌────────────────────┴─────────────────────┐
                    │                                          │
              dev ──▶ Seq (docker compose)      kind ──▶ OTel Collector ──▶ OpenSearch
                                                                    └─────▶ Dashboards
```

The application binary is **identical** in both environments. Only
`Observability:Otlp:Endpoint` differs. That is the whole reason for choosing OTLP over a
store-specific sink.

**Why a collector in the cluster but not in dev:** Seq ingests OTLP directly, so a collector
there would be a container earning nothing. OpenSearch does **not** ingest OTLP — it needs
either Data Prepper or the collector's `opensearch` exporter in front of it. The collector is
the lighter of the two and keeps the door open for adding traces and metrics to the same pipe.

---

### Task 1: OpenTelemetry foundation — packages, options, wiring

Nothing about how code *writes* a log changes in this task. It lands the export pipeline, which
is what makes the `traceId` work, and it does so with the exporter switched **off by default**
so the build stays green with no collector running.

**Files:**
- Modify: `src/Api/AiFramework.Api.csproj`
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj`
- Create: `src/Api/Observability/ObservabilityOptions.cs`
- Create: `src/Api/Observability/ObservabilityRegistration.cs`
- Create: `src/Infrastructure/Observability/InfrastructureTracing.cs`
- Modify: `src/Api/Program.cs`, `src/Api/appsettings.json`, `src/Api/appsettings.Development.json`
- Modify: `tests/Api.IntegrationTests/ApiFactory.cs`
- Test: `tests/Api.IntegrationTests/Observability/ObservabilityRegistrationTests.cs`

**Packages** — versions confirmed against nuget.org on 2026-09-13, latest stable:

| Package | Version | Project | Why |
|---|---|---|---|
| `OpenTelemetry.Extensions.Hosting` | 1.18.0 | Api | `AddOpenTelemetry()` host integration |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.18.0 | Api | the OTLP log + trace exporter |
| `OpenTelemetry.Instrumentation.AspNetCore` | 1.18.0 | Api | request spans, enrichment |
| `OpenTelemetry.Instrumentation.Http` | 1.18.0 | Api | outbound `HttpClient` spans |
| `OpenTelemetry.Instrumentation.Runtime` | 1.18.0 | Api | GC/thread-pool counters (cheap, deferred use) |
| `Npgsql.OpenTelemetry` | 10.0.3 | Infrastructure | database spans |

> **Use `Npgsql.OpenTelemetry`, not `OpenTelemetry.Instrumentation.EntityFrameworkCore`.** The
> latter has *never shipped a stable version* — its entire release history is `-beta.N`, current
> `1.18.0-beta.1`. `Npgsql.OpenTelemetry` 10.0.3 is stable and matches the
> `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 pin already in the csproj exactly. In a repo
> where a new analyzer version can break an untouched build (`Directory.Build.props`, line 28),
> a permanent beta on the data path is not a trade worth making.

**Why `Npgsql.OpenTelemetry` goes in Infrastructure, not Api:** `AddNpgsql()` is a
`TracerProviderBuilder` extension, so it must be called where the package is referenced. Putting
it in `Api` would mean `Api` carrying an Npgsql package reference — reaching past
`AddInfrastructure` into a storage concern, which `src/Api/CLAUDE.md` forbids. Instead
`Infrastructure` exposes one composition extension:

```csharp
// src/Infrastructure/Observability/InfrastructureTracing.cs
public static TracerProviderBuilder AddInfrastructureTracing(this TracerProviderBuilder builder)
```

and `Api` calls it, exactly as it calls `AddInfrastructure`. The `Api → Infrastructure, DI only`
rule is respected: this is composition, not a controller reaching into a repository.

**Produces:**

```csharp
public sealed class ObservabilityOptions
{
    public string ServiceName { get; set; } = "aiframework-api";
    public OtlpOptions Otlp { get; set; } = new();
}

public sealed class OtlpOptions
{
    // OFF by default. The default must be the one that works with nothing else running:
    // a developer who has not started Seq, and every CI job, must still get a green build.
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "http://localhost:5341/ingest/otlp";
    public bool Traces { get; set; } = true;
}
```

- [ ] **Step 1: Add the six packages, pinned.** Confirm each `<PackageReference>` carries an
      explicit `Version`. The five `OpenTelemetry.*` packages track their own release counter and
      do **not** follow the shared framework's `10.0.11` patch line — 1.18.0 is current and they
      are released together, so keep them on one version.

- [ ] **Step 2: `ObservabilityOptions`, bound in `Program.cs`.** Bound in `Program.cs` from
      `builder.Configuration.GetSection("Observability")`, mirroring how `CacheOptions` is bound
      there rather than inside `AddCaching` — and for the same reason recorded in
      `src/Infrastructure/CLAUDE.md`: a registration that binds configuration itself cannot be
      resolved from a bare `ServiceCollection` in a unit test.

- [ ] **Step 3: `AddObservability`.** One extension taking the `WebApplicationBuilder`, because it
      must reach both `builder.Logging` and `builder.Services`:

      - `builder.Logging.AddOpenTelemetry(o => { o.IncludeScopes = true; o.IncludeFormattedMessage = true; })`
        — `IncludeScopes` is load-bearing: it is what carries Task 3's `UserId` and operation
        scope onto each record.
      - `.WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
        .AddSource("Wolverine").AddInfrastructureTracing())`
      - `ResourceBuilder` with `ServiceName`, plus the pod name from `HOSTNAME` when present, so
        two replicas are distinguishable in the log store.
      - The OTLP exporter added **only when `Otlp.Enabled`**.

      **`AddSource("Wolverine")` is free money.** Wolverine 6 is already OpenTelemetry-
      instrumented — the committed generated handler at
      `src/Api/Internal/Generated/WolverineHandlers/OrderPlacedNotificationHandler1430415712.cs:25`
      calls `Activity.Current?.SetTag("message.handler", …)` today. Those tags are being written
      to nothing. One line collects them.

      **The tracer provider is registered even when the exporter is off.** That is deliberate and
      is what makes `Activity.Current` non-null, which is what makes the existing `traceId` lines
      work. Export and instrumentation are separate switches.

- [ ] **Step 4: Turn the exporter off in tests.** Add to `ApiFactory.ConfigureWebHost`, beside the
      existing `Cache:Enabled` and `RateLimiting:Auth:PermitLimit` settings and for the same
      class of reason:

      ```csharp
      builder.UseSetting("Observability:Otlp:Enabled", "false");
      ```

      Without it every integration test attempts OTLP delivery to a collector that is not there.
      The exporter does not throw — it retries and reports through its own `EventSource` — but it
      adds latency and noise to a suite that already shares one Postgres container. Belt and
      braces: the default is already `false`, so this line is defence against someone flipping
      the default later.

      **Do not wrap exporter setup in a `try`/`catch`.** OTLP failures are handled inside the SDK
      and never surface to application code; a catch here would be both unnecessary and a CA1031
      violation.

- [ ] **Step 5: Tests.** `Observability:Otlp:Enabled=false` still yields a resolvable
      `TracerProvider`; `Activity.Current` is non-null inside a request (assert via a test
      endpoint, or via a 500 response whose `traceId` now matches the W3C
      `^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$` shape — the stronger assertion, because it
      proves the payoff rather than the plumbing).

**Verification:** `dotnet build` clean in **both** Debug and Release — Release is not optional
here, per the note at the top of `.github/workflows/ci.yml`.

---

### Task 2: The logging behavior — the part that makes it seamless

This is the core of the plan. After it, **every command and query in the solution logs its
outcome and duration, and no handler contains a logging statement.**

**Files:**
- Create: `src/Infrastructure/Messaging/MessagingLog.cs`
- Modify: `src/Infrastructure/Messaging/Behaviors.cs`
- Modify: `src/Infrastructure/Messaging/MessagingRegistration.cs`
- Test: `tests/Infrastructure.Tests/Messaging/LoggingBehaviorTests.cs`
- Test: `tests/Infrastructure.Tests/Messaging/SensitiveCommandLoggingTests.cs`

**Produces:** `internal static partial class MessagingLog` holding the `[LoggerMessage]`
partials, and `Behaviors.LoggedAsync<TRequest, TResponse>` wrapping the existing chain.

`MessagingLog` is its own file rather than more members on `Behaviors`, mirroring `CacheScope`
being its own file: the `[LoggerMessage]` generator requires `partial`, and a partial `Behaviors`
that also holds four unrelated behaviors is harder to read than two focused files.

- [ ] **Step 1: `MessagingLog`.** A generic method cannot itself be a `[LoggerMessage]` target, and
      does not need to be — the type name is passed as a plain `string` parameter:

      ```csharp
      [LoggerMessage(Level = LogLevel.Debug,
          Message = "{Kind} {Name} succeeded in {ElapsedMs}ms")]
      internal static partial void Succeeded(ILogger logger, string kind, string name, long elapsedMs);

      [LoggerMessage(Level = LogLevel.Information,
          Message = "{Kind} {Name} failed with {ErrorCode} in {ElapsedMs}ms")]
      internal static partial void Failed(
          ILogger logger, string kind, string name, string errorCode, long elapsedMs);

      [LoggerMessage(Level = LogLevel.Warning,
          Message = "{Kind} {Name} threw after {ElapsedMs}ms")]
      internal static partial void Faulted(ILogger logger, string kind, string name, long elapsedMs);
      ```

      **Success is `Debug`, not `Information`, and that matters.** Every query goes through this.
      At `Information` — the current default level in `appsettings.json` — a single page load
      would emit a record per dispatch, and the store fills with "it worked". Failures are the
      thing worth keeping at default level.

      **Levels for failed `Result`s follow `ErrorKind`.** `Validation` and `NotFound` are the user
      being wrong, not the system: `Debug`. `Conflict` and `Unauthorized`: `Information`. Anything
      else: `Warning`. A 404 that pages someone is a broken logging system.

- [ ] **Step 2: `Behaviors.LoggedAsync`.**

      ```csharp
      internal static async Task<Result<TResponse>> LoggedAsync<TRequest, TResponse>(
          IServiceProvider sp, string kind, TRequest request,
          Func<Task<Result<TResponse>>> next)
      ```

      Three things it must get right:

      1. **`try`/`finally`, never `try`/`catch`.** CA1031 forbids the catch, and
         `GlobalExceptionHandler` already logs every unhandled exception at `Error` *with the
         exception object*. A local `outcome` variable initialised to "faulted" and assigned after
         the await gives correct duration and outcome on all three paths — success, failed
         `Result`, and thrown — with no catch and no double-reporting.
      2. **`Stopwatch.GetTimestamp()` / `Stopwatch.GetElapsedTime()`**, not `new Stopwatch()`.
         Allocation-free, and avoids a Meziantou allocation warning on a path every request takes.
      3. **A log scope carrying the operation name and `ICurrentUser.Id`**, opened around `next`,
         so anything the *handler* logs inherits the same context. `ICurrentUser` is resolved
         `GetService`, not `GetRequiredService`, and a null id is normal — the outbox pumps
         create scopes with no HTTP context at all, which is exactly what
         `src/Application/Abstractions/Ports.cs:9` documents.

- [ ] **Step 3: Wire it into both registration paths.** In `MessagingRegistration`, `LoggedAsync`
      becomes the **outermost** wrapper, so its duration includes validation, commit and
      eviction — which is what "how long did this command take" means to an operator:

      ```csharp
      // AddCommand: Logged( Validate → handler → Commit → Evict )
      // AddQuery:   Logged( Cached( handler ) )
      ```

      Both `InvokeAsync` local functions stay `static` and capture nothing, preserving the
      reflection-free, trim-safe dispatch that `src/Infrastructure/CLAUDE.md` explicitly warns
      against "simplifying" away.

- [ ] **Step 4: `LoggingBehaviorTests`.** A fake `ILoggerProvider` capturing records; assert a
      successful command logs at `Debug` with its type name, a failed `Result` logs at the level
      its `ErrorKind` maps to and includes the error *code*, and a throwing handler still logs
      `Faulted` and **rethrows** (the exception must reach `GlobalExceptionHandler`).

- [ ] **Step 5: `SensitiveCommandLoggingTests` — the load-bearing one.**

      > **The behavior logs `typeof(TRequest).Name` and never the request instance.** This is a
      > security constraint, not a style preference. `SignIn`, `RegisterUser` and `ChangePassword`
      > are records whose fields include the plaintext password
      > (`src/Application/Users/SignIn.cs:7`). A behavior that logged the request object — the
      > obvious "improvement" for a future contributor wanting richer logs — would write every
      > password in the system to the log store, in plaintext, permanently. It is the same class
      > of silent, uncatchable-by-ordinary-tests mistake as a cache key missing its user scope
      > (ADR 0009), and it gets the same treatment: a test that fails loudly.

      Dispatch a real `SignIn` carrying a known sentinel password through a real pipeline with a
      capturing logger provider, and assert **no emitted record's rendered text or state contains
      the sentinel**. Behavioral, not structural, so it keeps holding however the behavior is
      later rewritten.

      Note the deliberate consequence: log records identify *which* command ran and how it ended,
      never *what it contained*. Argument-level detail belongs in a span attribute on a
      per-command opt-in basis, not in a blanket behavior — deferred, not forgotten.

**Verification:** `dotnet test`. `RegistrationCompletenessTests` must still pass untouched — it
reflects over registrations and is the net for this whole registration path.

---

### Task 3: Correlation and the `Application` layer question

**Files:**
- Modify: `src/Application/AiFramework.Application.csproj`
- Modify: `frontend/src/api/client.ts`
- Test: `frontend/src/api/client.test.ts`
- Test: `tests/Application.Tests/ArchitectureTests.cs`

- [ ] **Step 1: Decide — may `Application` log?** **Recommendation: yes, but only
      `Microsoft.Extensions.Logging.Abstractions`.**

      The hook permits it (verified above: not on `Application`'s banned list), and
      `ArchitectureTests.Application_references_Domain_only` only inspects `AiFramework.*`
      assemblies, so neither guard would object. The question is therefore whether it *should*.

      ADR 0009's precedent — markers in `Application`, mechanism in `Infrastructure` — was about a
      *caching package*, which brings a store, a serializer and a policy engine. `ILogger<T>` is
      not that: `Microsoft.Extensions.Logging.Abstractions` is interfaces only, ships with the
      shared framework, and is the canonical port shape. Banning it would push a handler that has
      something genuinely worth saying into inventing a bespoke port for it.

      The rule to write down is therefore about *behavior*, not packages:
      **`Application` may inject `ILogger<T>` for a deliberate, domain-meaningful statement. It
      must not log control flow that the behavior already reports** — no "handling X", no
      "returning failure", no entry/exit. The behavior owns those, uniformly.

      Add `Microsoft.Extensions.Logging.Abstractions` **only if a handler actually needs it**; the
      preferred outcome of this plan is that none does yet. Extend
      `Application.Tests/ArchitectureTests` with an assertion that `Application` references no
      logging *implementation* or *sink* package — only the abstractions — so the door stays open
      exactly as wide as it was opened.

- [ ] **Step 2: Surface `traceId` in the frontend.** `ApiError` (`frontend/src/api/client.ts:7`)
      currently parses `ProblemDetails` and **drops `traceId` on the floor**. Add it to the
      interface and the class, so an error surface can show a correlation id the operator can
      paste into Seq or Dashboards. This is the last link in the chain and costs about six lines.

      No contract regeneration: `traceId` is already in the response body, `client.ts` is
      hand-written, and `schema.d.ts` is untouched.

---

### Task 4: Background and outbox logging

The outbox already logs three messages. What it lacks is *context*: `OutboxWorkItemProcessor`
decides retry-vs-dead-letter and records none of it.

**Files:**
- Modify: `src/Infrastructure/Outbox/OutboxWorkItemProcessor.cs`
- Test: `tests/Infrastructure.Tests/Outbox/OutboxWorkItemProcessorTests.cs`

- [ ] **Step 1: Log the three outcomes** — dispatched, retry scheduled (with `Attempt` and
      `NextAttemptAt`), dead-lettered (`Warning`; a message giving up after `MaxAttempts` is the
      single most operationally interesting event the outbox produces, and today it is silent).
- [ ] **Step 2: A log scope** carrying `MessageId`, `EventType` and `Attempt` around processing, so
      every record from a handler inherits them. `MessageId` is stable across redeliveries
      (`src/Application/CLAUDE.md`), which makes it the right key for "show me every attempt at
      this message".
- [ ] **Step 3: Do not touch the two `catch (Exception)` blocks** in `OutboxHostedServices.cs`.
      They hold the repo's only CA1031 exemption, they already log, and the `.editorconfig`
      exemption is file-scoped — widening it is not in scope.

---

### Task 5: Outbox trace continuity *(optional — needs a migration)*

**Recommend deferring this to its own change.** It is the highest-value remaining gap and the
only task here that touches the database.

The problem: a domain event raised inside a request is delivered later, by a background pump, in
a different `Activity` context. The trace breaks at the outbox boundary — an `OrderPlaced` audit
write cannot be correlated back to the `POST /api/orders` that caused it.

The fix: persist the W3C `traceparent` alongside the message and restore it on processing.

**Files:** `OutboxMessage.cs`, `OutboxMessageConfiguration.cs`, `DomainEventsInterceptor.cs`,
`OutboxWorkItemProcessor.cs`, plus a new migration.

- [ ] Add a nullable `TraceParent` column (nullable is required — rows written before this change
      have none, and rows written with no ambient `Activity` legitimately have none).
- [ ] Capture `Activity.Current?.Id` in `DomainEventsInterceptor.SavingChangesAsync`. It must stay
      in `SavingChangesAsync`, for the atomicity reason `src/Infrastructure/CLAUDE.md` spells out
      at length.
- [ ] In the processor, start an `Activity` with the stored parent so the delivery span links to
      the originating request.
- [ ] `dotnet ef migrations add AddOutboxTraceParent --project src/Infrastructure --startup-project src/Infrastructure`.
      **Add a migration; never edit one.** `.claude/hooks/protect-migrations.ps1` keys on git
      history, so an uncommitted new migration is still adjustable.
- [ ] `OutboxAtomicityTests` and `OutboxDeliveryTests` must stay green — this touches the
      interceptor those tests exist to protect.

---

### Task 6: Dev stack — Seq

**Files:** `docker-compose.yml`, `src/Api/appsettings.Development.json`, `scripts/dev.ps1`,
`scripts/stop-dev.ps1`, `local-run/control-panel.bat`

- [ ] **Step 1: Add a `seq` service.** `datalust/seq:latest`, `ACCEPT_EULA=Y`, a named volume for
      persistence (matching how the dev Postgres keeps its data, unlike the throwaway e2e one).
      Port via `${SEQ_PORT:-55341}` → container `80`, following the established
      `DEV_PG_PORT`/`PG_PORT` override convention exactly.
- [ ] **Step 2: Point Development at it.** In `appsettings.Development.json`, set
      `Observability:Otlp:Enabled=true` and the endpoint to the Seq ingest URL. Not a secret,
      committed, same judgement as the dev connection string already in that file.

      > **Verify Seq's OTLP path at implementation time rather than trusting this document.** Seq's
      > OTLP ingestion (`/ingest/otlp/v1/logs`) arrived in a specific Seq version and the exporter
      > appends the signal path itself, so endpoint-vs-full-URL configuration is easy to get
      > subtly wrong. Confirm against the pulled image's own docs before writing the value down.

- [ ] **Step 3: Make it optional, not mandatory.** `docker compose up -d --wait` gaining a second
      container slows the inner loop for everyone, including anyone who never opens Seq. Put it
      behind a compose **profile** so `./scripts/dev.ps1` is unchanged by default and Seq starts
      only when asked. `Observability:Otlp:Enabled` is what actually decides whether the app
      exports, so a developer who skips Seq still gets a working build and stdout logs.
- [ ] **Step 4: `stop-dev.ps1` and the control panel menu** learn about it, so the
      double-clickable path stays complete.

---

### Task 7: Kubernetes — OpenTelemetry Collector → OpenSearch

**Files:** `k8s/base/…` (new `observability/` directory), `k8s/overlays/local/config.yaml`,
`k8s/overlays/local/kustomization.yaml`, `deploy/deploy.ps1`

**Honest sizing warning, up front.** The kind cluster already runs Postgres, two API replicas,
the web pod and ingress-nginx on a laptop. OpenSearch is a JVM that wants ~1 GB of heap to be
comfortable, and OpenSearch Dashboards is a second Node process on top. This will be the largest
thing in the cluster by a wide margin.

- [ ] **Step 1: Make the whole thing an opt-in kustomize component**, not part of the default
      overlay, and give `deploy/deploy.ps1` a `-WithObservability` switch. A developer rehearsing
      the cookie-affinity behaviour of ADR 0010 should not pay for a log store to do it.
- [ ] **Step 2: OTel Collector** (`otel/opentelemetry-collector-contrib`) as a Deployment plus a
      ConfigMap: OTLP receiver, batch processor, `opensearch` exporter. The collector is what
      bridges OTLP to OpenSearch — **OpenSearch does not ingest OTLP natively**, and this is the
      most common way this architecture is got wrong.
- [ ] **Step 3: OpenSearch single-node** StatefulSet with
      `discovery.type=single-node`, `DISABLE_SECURITY_PLUGIN=true` (local-only cluster, same
      judgement already applied to the committed throwaway credentials in `secret.yaml`, and
      the header comment must say so in the same voice), and
      `OPENSEARCH_JAVA_OPTS=-Xms512m -Xmx512m` with matching resource limits. Plus Dashboards.
- [ ] **Step 4: Config, with double underscores.** In `config.yaml`:
      `Observability__Otlp__Enabled: 'true'` and
      `Observability__Otlp__Endpoint: 'http://otel-collector.aiframework:4318'`. Single
      underscores bind nothing and warn nothing — the trap `CLAUDE.md` already documents twice.
- [ ] **Step 5: Index lifecycle.** An ISM policy rolling indices daily and deleting after 7 days.
      Without it a local cluster grows without bound until the kind node runs out of disk, which
      surfaces as unrelated pods being evicted.
- [ ] **Step 6: Do not add this to `deploy/e2e-k8s.ps1`'s gate.** That script gates readiness on
      `/api/auth/me` for reasons ADR 0012 records; a log store has no business in that path.

---

### Task 8: Documentation

- [ ] **Root `CLAUDE.md`** gains a "Logging" section: the behavior is automatic, do not log entry
      and exit by hand, success is `Debug`, the behavior never logs a request instance and why,
      and the double-underscore config keys.
- [ ] **`src/Infrastructure/CLAUDE.md`** gains `LoggedAsync` in the behaviors list beside the
      caching entries.
- [ ] **`src/Application/CLAUDE.md`** records the Task 3 decision: `ILogger<T>` permitted for
      deliberate statements, never for control flow the behavior already covers.
- [ ] **`tests/CLAUDE.md`** records `SensitiveCommandLoggingTests` alongside the existing
      "deleting this removes the only net" notes — because that is exactly what it is.
- [ ] **ADR 0014** — `/adr "Centralized logging over OTLP"`. Context: two logging files and a
      `traceId` that resolves to nothing. Decision: MEL + `[LoggerMessage]` as the writing API,
      OTLP as transport, a pipeline behavior rather than a base class, store chosen by config.
      Consequences: `Activity.Current` becomes non-null and the `traceId` format changes; the
      behavior deliberately logs no request content; OpenSearch is opt-in in the cluster.

> **Unrelated gap found while researching this.** Root `CLAUDE.md` cites **ADR 0011** six times
> (the whole "Session invalidation" section, and `SessionClaims.SecurityStamp`), but
> `docs/adr/0011-*.md` does not exist and `git log --all --diff-filter=A` shows it was never
> committed. The ADR directory jumps 0010 → 0012. Not caused by this work and not fixed by it —
> flagged because the next ADR number is 0014 and someone should decide whether 0011 gets
> back-written.

---

## Sequencing

| Phase | Tasks | Outcome |
|---|---|---|
| 1 | 1, 2 | Every command and query logs, correlated, to stdout. **The `traceId` starts working.** No new infrastructure. |
| 2 | 3, 4, 6 | Frontend surfaces the id, outbox is diagnosable, Seq gives a real dev UI. |
| 3 | 7 | OpenSearch in the cluster, opt-in. |
| 4 | 5 | Trace continuity across the outbox. Own change, own migration. |

**Phase 1 is independently valuable and ships without a single new container.** If the appetite
for OpenSearch changes, nothing in Phase 1 is wasted — that is the point of exporting OTLP rather
than writing to a store.

## Explicitly out of scope

- **Metrics and dashboards.** The OTel packages make `.WithMetrics()` a few lines away, but this
  is a logging plan and mixing in an SLO conversation would widen it past reviewable.
- **Frontend error telemetry** (sending browser errors to the backend). Real, separate, and
  needs a privacy decision first.
- **Log-based alerting.**
- **Audit logging.** `OrderAuditWriter` already exists and writes to Postgres. Audit records are
  business data with retention requirements, not diagnostics — they must not be migrated onto
  this pipeline.
