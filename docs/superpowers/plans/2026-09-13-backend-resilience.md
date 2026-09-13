# Backend Resilience Implementation Plan

> **For agentic workers:** Steps use checkbox (`- [ ]`) syntax for tracking. Implement
> task-by-task, verifying each before starting the next.

**Goal:** Transient failures — a Postgres pod restarting, a third party answering 503 — stop
surfacing as 500s. Retry lives at exactly one layer per call path, with a bounded budget, and
the unsafe retry is awkward to write.

**Architecture:** `Microsoft.Extensions.Http.Resilience` in Infrastructure only, attached to
typed clients under the Application port (never over it, because Polly cannot see a failed
`Result<T>`). `EnableRetryOnFailure` on Npgsql with a budget small enough to fit inside the
readiness probe. A fifth `ErrorKind` so exhausted retries answer 503 + `Retry-After` instead of
500. One reference integration — read-only exchange rates — because it is the only place where
retry, the shared cache factory, the total timeout and the new `ErrorKind` meet.

**Tech Stack:** .NET 10 (net10.0), `Microsoft.Extensions.Http.Resilience` 10.10.0 (Polly v8),
`Microsoft.Extensions.TimeProvider.Testing` 10.10.0, EF Core + Npgsql 10.0.3, xUnit +
FluentAssertions + NSubstitute, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-13-backend-resilience-design.md`

## Global Constraints

- **Warnings are errors** — compiler, analyzers and build. Never suppress without a narrow
  `#pragma warning disable`/`restore` pair carrying a justification comment above it.
- **Nullable is enabled.** **Never `catch (Exception)`** — CA1031 is an error outside the
  outbox's file-scoped exemption. `throw;`, never `throw ex;`.
- **The dependency rule is hook-enforced.** `Microsoft.Extensions.Http.Resilience` and `Polly`
  may appear in `AiFramework.Infrastructure.csproj` and nowhere else. Application gets the port
  and nothing more — the same rule it already states for caching packages.
- **Never return a failed `Result` from inside a Polly-wrapped delegate.** Polly inspects
  outcomes; a `Result` failure reads as a success and silently disables the retry.
- **Handlers never call `SaveChangesAsync`.** Unchanged by this plan.
- **No secrets in `appsettings*.json`.** The reference provider is key-less for this reason;
  `.claude/hooks/no-secrets.ps1` blocks the alternative.
- **No `Thread.Sleep`, no `Task.Delay`, no retry-until-timeout in tests**
  (`tests/Api.IntegrationTests/ApiFactory.cs:204`). Backoff is asserted with `FakeTimeProvider`.
- **Every new command and query must be registered in `AddMessaging()`** — the
  registration-completeness tests in `Infrastructure.Tests` fail the build otherwise.
- Run `dotnet test <project>` **one project at a time** — two paths in one invocation fails
  with MSB1008.
- Dev connection string for `dotnet ef`:
  `Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres`
- Branch: `claude/backend-retry-policies-imo9we`.

---

## File Structure

**Created:**
- `src/Infrastructure/Resilience/ResilienceOptions.cs` — the kill switch and the four numbers.
- `src/Infrastructure/Resilience/ResilienceRegistration.cs` — `AddResilience()`.
- `src/Infrastructure/Resilience/ExchangeRateClient.cs` — the reference typed client.
- `src/Application/Orders/GetExchangeRate.cs` — query, response record, handler.
- `src/Application/Abstractions/IExchangeRateProvider.cs` — the port.
- `src/Api/Rates/RatesController.cs`, `src/Api/Rates/RateDtos.cs`
- `tests/Infrastructure.Tests/Resilience/ResilienceRegistrationTests.cs`
- `tests/Infrastructure.Tests/Resilience/ExchangeRateClientTests.cs`
- `tests/Infrastructure.Tests/Resilience/StubHttpMessageHandler.cs`
- `tests/Application.Tests/Orders/GetExchangeRateHandlerTests.cs`
- `tests/Api.IntegrationTests/Rates/RatesEndpointTests.cs`
- `docs/adr/0014-retry-and-resilience-policies.md`

**Modified:**
- `src/Infrastructure/AiFramework.Infrastructure.csproj` — the resilience package.
- `src/Infrastructure/InfrastructureRegistration.cs` — `EnableRetryOnFailure`, `AddResilience()`.
- `src/Infrastructure/Outbox/OutboxPoller.cs` — comment only, explaining the deliberate gap.
- `src/Application/Abstractions/Result.cs` — `ErrorKind.Unavailable`.
- `src/Api/ResultExtensions.cs` — the 503 mapping and the `Retry-After` header.
- `src/Api/Program.cs` — bind the `"Resilience"` section.
- `src/Api/appsettings.json` — the `"Resilience"` defaults.
- `k8s/base/api.yaml` — explicit `timeoutSeconds` on the readiness probe.
- `k8s/overlays/local/secret.yaml` — `Timeout=5` on the connection string.
- `tests/Api.IntegrationTests/ApiFactory.cs` — `Resilience:Enabled=false`, stub base address.
- `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts` — regenerated.
- `CLAUDE.md`, `src/Application/CLAUDE.md`, `src/Infrastructure/CLAUDE.md`.

**Deliberately unchanged:** `src/Application/Abstractions/Ports.cs` (`IClock` is not widened
into a `TimeProvider`), `src/Infrastructure/Outbox/OutboxWorkItemProcessor.cs` (it already has
backoff), `frontend/src/api/client.ts` (`ApiError` already carries an arbitrary status).

---

### Task 1: Options and the kill switch, wired but inert

Land the configuration surface first, so every later task has a switch to turn off and the
test host has something to neutralise.

**Files:**
- Create: `src/Infrastructure/Resilience/ResilienceOptions.cs`
- Create: `src/Infrastructure/Resilience/ResilienceRegistration.cs`
- Modify: `src/Infrastructure/InfrastructureRegistration.cs`
- Modify: `src/Api/Program.cs`, `src/Api/appsettings.json`
- Create: `tests/Infrastructure.Tests/Resilience/ResilienceRegistrationTests.cs`

**Steps:**
- [ ] `ResilienceOptions` with `Enabled` (default `true`), `TotalRequestTimeout` (30s),
      `AttemptTimeout` (10s), `MaxRetryAttempts` (3), `BaseDelay` (2s), and
      `ExchangeRateBaseAddress` (`https://api.frankfurter.dev/v1` — changed from the
      `.app` domain named in the design docs above once implementation found `.app` already
      past its own Deprecation header and redirecting; see ResilienceOptions.cs's remarks).
- [ ] `AddResilience()` registers and validates the options, and **does not bind
      configuration** — copy `CachingRegistration`'s `<remarks>` reasoning verbatim in spirit:
      binding here would make `IOptions<ResilienceOptions>` require an `IConfiguration` that a
      bare `ServiceCollection` in a unit test does not have.
- [ ] Validate what would otherwise fail quietly: `AttemptTimeout <= TotalRequestTimeout`
      (the standard handler throws at startup otherwise, and the message is obscure),
      `MaxRetryAttempts >= 0`, `BaseDelay > TimeSpan.Zero`, base address is absolute.
- [ ] Call `AddResilience()` from `AddInfrastructure`, beside `AddCaching()`.
- [ ] `Program.cs`: `builder.Services.Configure<ResilienceOptions>(builder.Configuration.GetSection("Resilience"))`,
      next to the existing `CacheOptions` line and with the same one-line reason.
- [ ] `appsettings.json`: a `"Resilience"` block mirroring the defaults.

**Verify:** `dotnet test tests/Infrastructure.Tests` — the options resolve from a bare
`ServiceCollection`; each invalid value fails validation with its own message.

---

### Task 2: `ErrorKind.Unavailable` reaches the wire as 503 + `Retry-After`

Before anything can fail, decide what failure looks like.

**Files:**
- Modify: `src/Application/Abstractions/Result.cs`, `src/Api/ResultExtensions.cs`
- Modify: `tests/Api.IntegrationTests/Diagnostics/TestEndpointsStartupFilter.cs` (if a probe
  endpoint is the cheapest way to assert the mapping without a real provider)

**Steps:**
- [x] Add `Unavailable` to `ErrorKind` with an XML comment saying what it means and, more
      importantly, what it does not: an expected, *retryable* upstream failure, not a bug.
      A bug is an exception and belongs in `GlobalExceptionHandler`'s 500 branch.
- [x] Map it to `StatusCodes.Status503ServiceUnavailable` in `ResultExtensions.Problem`.
- [x] Set `Retry-After` on the 503 — the first response *header* this method writes. Use
      `Math.Ceiling`, and say why in a comment: `AuthRateLimitTests` already proves that
      truncating produces `Retry-After: 0`, which sends a well-behaved client straight back in.
      Extended beyond the plan: `Error` gained an optional `RetryAfter` (`TimeSpan?`) so a
      producer with a better number can supply one, with a documented 5s floor when it does not.
- [x] Leave every other `ErrorKind` mapping untouched.

**Verify:** `dotnet test tests/Api.IntegrationTests` — a failed `Result` carrying
`ErrorKind.Unavailable` produces 503, `application/problem+json`, a `traceId`, and a
`Retry-After` strictly greater than zero.

**Done.** `ResultExtensionsTests.cs` (new, unit-level, no host) covers all five `ErrorKind`
mappings plus the Retry-After fallback/rounding/non-positive-override branches — 15 tests, all
green. `TestEndpointsStartupFilter` gained a third probe path
(`/api/test/problem/unavailable`) since no real handler produces `Unavailable` yet, executing
the exact `result.Problem(HttpContext)` call site a controller uses via
`ObjectResult.ExecuteResultAsync`; `Diagnostics/ProblemMappingTests.cs` proves the header
reaches the wire over real HTTP — this one needs the shared Postgres Testcontainer for
`CreateAuthenticatedClientAsync` and could not run in this container (no Docker daemon), but it
builds clean and its only recorded failure mode here is `DockerUnavailableException`, the same
as every other Testcontainers-backed test. Debug and Release both build with zero warnings.

---

### Task 3: EF transient retry, with the rules it creates

**Files:**
- Modify: `src/Infrastructure/InfrastructureRegistration.cs`
- Modify: `src/Infrastructure/Outbox/OutboxPoller.cs` (comment only)
- Modify: `k8s/base/api.yaml`, `k8s/overlays/local/secret.yaml`
- Modify: `src/Infrastructure/CLAUDE.md`
- Modify (discovered, not planned): `src/Infrastructure/EventPath/WolverineEventPath.cs`,
  `tests/Api.IntegrationTests/EventPath/WolverineOutboxAtomicityTests.cs`

**Steps:**
- [x] `UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3,
      maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null))`, with a comment deriving
      the 1-second cap from the readiness probe rather than stating it as taste.
- [x] Comment above `OutboxPoller.ClaimAsync`'s raw `NpgsqlCommand`: it bypasses the execution
      strategy by construction, and that is deliberate — the next poll cycle and the
      `LeasedUntil` clause already recover a failed claim twice over.
- [x] `k8s/base/api.yaml`: `timeoutSeconds: 5` on the readiness probe, with a comment saying
      the Kubernetes default is 1s and that `CanConnectAsync` now goes through the strategy.
- [x] `k8s/overlays/local/secret.yaml`: add `Timeout=5` to the connection string, with a
      comment that Npgsql's 15s default is the real floor on a blackholed host, not the retry
      delay.
- [x] `src/Infrastructure/CLAUDE.md`, under **EF rules**: explicit transactions must go through
      `CreateExecutionStrategy().ExecuteAsync(...)`; a bare `BeginTransactionAsync` now throws.
      Note there are none in `src/` today, which is why this is cheap to adopt now.
- [x] `src/Application/Users/SignIn.cs`: one comment on `RecordFailureAsync`'s call site (the
      plan named this method `RecordSignInOutcomeAsync`; the actual method is
      `RecordFailureAsync`, calling `IUserRepository.TryRecordFailedSignInAsync`) recording that
      a retried `ExecuteUpdateAsync` can double-count a failure, and that over-counting is
      ADR 0008's preferred direction. Traced through the actual compare-and-swap implementation
      (`WHERE FailedSignInAttempts = expected`) rather than asserted generically: EF's own retry
      of an already-committed-but-unacknowledged write is indistinguishable, from
      `TryRecordFailedSignInAsync`'s single boolean return, from a genuine concurrent writer —
      both read as "the counter already moved" — so `RecordFailureAsync`'s own one-retry-on-loss
      logic re-reads and re-advances a counter EF's strategy had already advanced once.

**Found, not planned:** `EnableRetryOnFailure` broke
`WolverineOutboxAtomicityTests.PublishedThroughTheOutbox_ThenSaved_IsDeliveredAndTheOrderIsPersisted`,
which calls Wolverine's `IDbContextOutbox<AiFrameworkDbContext>.SaveChangesAndFlushMessagesAsync`
directly — that method opens its own transaction internally, and a retrying execution strategy
refuses to run one un-wrapped ("does not support user-initiated transactions"). ADR 0005's spike
is not called from any production handler today, so this was not a production regression, but it
is exactly the shape a real handler adopting `IDbContextOutbox<T>` would hit. Fixed by wrapping
the test's call in `context.Database.CreateExecutionStrategy().ExecuteAsync(...)` — proving the
required pattern rather than hiding it behind a test-only workaround — with a matching comment
on `WolverineEventPath.cs`'s `UseEntityFrameworkCoreTransactions()` call so the interaction is
documented at its source, not only where a test happened to trip over it.

**Verify:** `dotnet test tests/Infrastructure.Tests` (Testcontainers path still green — the
strategy changes how a failure is handled, not how a success behaves), then
`dotnet test tests/Api.IntegrationTests`.

**Done, verified with a real Postgres container** (Docker was available in this environment,
unlike Tasks 1–2): all 425 tests across all four projects pass — Domain 74, Application 96,
Infrastructure 144 (including every Testcontainers-backed `Persistence`/`Outbox` test), Api 111
(including `WolverineOutboxAtomicityTests` and `WolverineCodegenTests`). Debug and Release both
build with zero warnings; Api.IntegrationTests also re-run clean under `-c Release`.
`dotnet run --project src/Api -- codegen write` produces no diff, as expected — nothing here
touches a Wolverine handler.

---

### Tasks 4 and 5: The port, the query, the typed client and its pipeline

**Merged into one unit of work, discovered while implementing Task 4 in isolation.** The plan
put the port+query (Task 4) and the typed client (Task 5) in separate commits, each independently
green. That assumption was wrong: the moment `GetExchangeRate : IQuery<>` exists, it must be
registered in `AddMessaging()` or `RegistrationCompletenessTests` fails the build — but
registering it wires `GetExchangeRateHandler`, whose constructor needs `IExchangeRateProvider`,
into the container `AddInfrastructure` builds for every `Api.IntegrationTests` host. A real
`WebApplicationFactory` host runs with `ServiceProviderOptions.ValidateOnBuild = true` (the
non-Production default) and validates the WHOLE service graph at build time — so registering the
query without yet having an `IExchangeRateProvider` implementation broke `HealthTests` and
every other Api test that boots a real host, all 111 of them, with no way to land Task 4 alone
and keep the build green. Confirmed by doing exactly that and watching them fail. Fixed by
building both tasks together instead of inventing a throwaway stub implementation to bridge the
gap.

**File-location deviation from the plan, decided before writing code.** The plan put
`IExchangeRateProvider.cs` in `Application/Abstractions/` and `GetExchangeRate.cs` in
`Application/Orders/`. Neither matches this repo's own convention: `IOrderRepository`,
`IProductRepository`, and `IUserRepository` all live beside their feature, not in
`Abstractions/` (that folder is for cross-cutting ports like `IClock` and `ICurrentUser`), and
exchange rates have nothing to do with Orders. Both files went into a new `Application/Rates/`
folder instead — matching Api's own Task 6 file list, which already puts `RatesController.cs`
in its own `Api/Rates/` folder rather than nesting it under Orders. Test files followed:
`tests/Application.Tests/Rates/GetExchangeRateHandlerTests.cs`, not `.../Orders/...`.

**Files (as actually created/modified):**
- Create: `src/Application/Rates/IExchangeRateProvider.cs` (port + `ExchangeRate` record)
- Create: `src/Application/Rates/GetExchangeRate.cs` (query, `ExchangeRateView`, handler)
- Create: `tests/Application.Tests/Rates/GetExchangeRateHandlerTests.cs` (13 tests)
- Modify: `src/Infrastructure/InfrastructureRegistration.cs` — registers `GetExchangeRate` in
  `AddMessaging()` and calls `AddExchangeRateClient()` in `AddInfrastructure()`
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj` — `Microsoft.Extensions.Http.Resilience`
  10.10.0, plus three existing `Microsoft.Extensions.*` pins bumped 10.0.11 → 10.0.12 (its
  transitive graph floors them there; NU1605 package-downgrade is a build error in this repo)
- Create: `src/Infrastructure/Resilience/ExchangeRateClient.cs`
- Modify: `src/Infrastructure/Resilience/ResilienceRegistration.cs` — `AddExchangeRateClient()`
- Modify: `src/Infrastructure/Resilience/ResilienceOptions.cs` — corrected doc comments (below)
- Create: `tests/Infrastructure.Tests/Resilience/StubHttpMessageHandler.cs`
- Create: `tests/Infrastructure.Tests/Resilience/ExchangeRateClientTests.cs` (10 tests)
- Modify: `tests/Infrastructure.Tests/Resilience/ResilienceRegistrationTests.cs` — one new
  wiring test, plus corrected `MaxRetryAttempts` validation tests (below)
- Modify: `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj` —
  `Microsoft.Extensions.TimeProvider.Testing` 10.10.0
- Modify: `src/Api/appsettings.json` — `ExchangeRateBaseAddress` updated to `.dev` (below)

**Steps:**
- [x] `IExchangeRateProvider.GetRateAsync(string baseCurrency, string quoteCurrency, CancellationToken)`
      returning `Task<Result<ExchangeRate>>`. No `HttpClient`, no Polly type, no EF type.
- [x] `GetExchangeRate(string From, string To) : IQuery<ExchangeRateView>, ICacheable`. `CacheKey`
      upper-cases both codes (`"eur"`/`"EUR"` must not be two cache entries for one rate) —
      one step beyond the plan's literal `$"{From}:{To}"`. `Duration` is one minute, not thirty
      seconds like `GetOrders`/`GetProducts`: caching this query is as much about not spending a
      third party's own rate limit as it is about latency.
- [x] The handler validates its own inputs (three ASCII letters, via `value is { Length: 3 } &&
      value.All(char.IsAsciiLetter)`, null-safe) and returns `ErrorKind.Validation` before ever
      calling the port.
- [x] The handler passes the provider's failed `Result` straight through unchanged — no retry,
      no translation, no catch.
- [x] `PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="10.10.0"` in
      Infrastructure only; `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 in
      `Infrastructure.Tests` only. Both required bumping three existing `10.0.11` pins to
      `10.0.12` to clear an `NU1605` package-downgrade error from Http.Resilience's own
      transitive floor.
- [x] `AddHttpClient<IExchangeRateProvider, ExchangeRateClient>()` configuring `BaseAddress` from
      `ResilienceOptions` (normalized to always end in `/` — `Uri` silently drops a `BaseAddress`
      without a trailing slash's last path segment), then `.AddStandardResilienceHandler()`
      configured from the same options via a lazy `.Configure((options, sp) => ...)` callback.
- [x] A comment above the client and above `AddExchangeRateClient` recording the strategy order
      and why it is not rearranged: total timeout outside the retry, attempt timeout inside it.
- [x] `ExchangeRateClient` converts only the **final** outcome to `Result<T>`: `HttpRequestException`,
      `TimeoutRejectedException`, and `BrokenCircuitException` escaping the pipeline, or any
      non-success status other than 400/404, become `ErrorKind.Unavailable`; 400 becomes
      `Validation`; 404 becomes `NotFound`. Each caught type is specific — CA1031 stayed an error
      throughout, with no exemption added.
- [x] A comment in the adapter and in `ResilienceOptions`'s remarks stating the rule from decision
      2: nothing inside the pipeline may return a failed `Result`.
- [x] `ExchangeRateClientTests` drives a stub `HttpMessageHandler` through the real pipeline: a
      503 then a 200 retries and succeeds; four consecutive 503s (default `MaxRetryAttempts = 3`,
      so one initial attempt plus three retries) exhaust and return `Unavailable`; a 400 and a
      404 are each **not** retried (stub called exactly once); a 429's `Retry-After` is honoured
      over the computed backoff (proven by simulated elapsed time, not just that a retry
      happened); `Enabled = false` calls exactly once regardless of failures. No real network, no
      real wall-clock delay — see the two corrections below for what that actually took.

**Two real defects found by writing these tests, neither hypothetical:**

1. **`MaxRetryAttempts = 0` does not mean "never retry" — it throws.** The design assumed
   `Enabled = false` could zero `MaxRetryAttempts` to disable retrying, and both `ResilienceOptions`'s
   validation (`>= 0`) and its doc comment ("Zero is valid...") were written around that
   assumption. Polly's own `RetryStrategyOptions<T>.MaxRetryAttempts` validation requires **at
   least 1** and throws `OptionsValidationException` — not at startup, where `ValidateOnStart`
   could catch it, but from inside the pipeline-build callback the first time a request is made.
   Found by every retry-based test in `ExchangeRateClientTests` failing identically the first
   time `Enabled = false` was exercised. Fixed by validating `MaxRetryAttempts >= 1` instead, and
   implementing "disabled" as `options.Retry.ShouldHandle = _ => ValueTask.FromResult(false)` —
   `MaxRetryAttempts` stays at whatever it is configured to; the predicate just means it is never
   consulted. `ResilienceOptions.cs`'s doc comments on both `Enabled` and `MaxRetryAttempts`, this
   plan's Global Constraints section having named the old (wrong) mechanism nowhere directly, and
   `ResilienceRegistrationTests`'s zero/one-attempt validation tests were all updated to match.
2. **`FakeTimeProvider` needs manual `Advance()` calls, not `AutoAdvanceAmount`, and needs a
   `Task.Yield()` between them under xUnit specifically.** `AutoAdvanceAmount` only advances the
   clock when something repeatedly *reads* it in a polling loop; Polly's retry and timeout
   delays each schedule exactly one timer via `TimeProvider.CreateTimer` and await its single
   callback, so `AutoAdvanceAmount` is never consulted and the timer never fires — confirmed by a
   standalone repro that hung past 30 real seconds with it set. The working pattern: kick the
   call off without awaiting it, then loop calling `clock.Advance(step)`, which fires any due
   timer synchronously. That alone was enough in a plain console repro built against the real
   `AiFramework.Infrastructure` project (proving the registration code itself was correct), but
   hung indefinitely inside every retry-based xUnit test specifically — xUnit's test execution
   context posts the timer callback's continuation rather than running it inline, and nothing
   pumps that post without an actual async yield point in the loop. Fixed by making the advance
   loop `async` and inserting `await Task.Yield()` between each `Advance()` call — a scheduler
   bounce with no real delay of its own, just enough to let xUnit's context run what `Advance()`
   already queued. `AdvanceUntilCompleteAsync<T>`'s own remarks in `ExchangeRateClientTests.cs`
   record both findings in full so nobody rediscovers either one from scratch.

**One more correction, unrelated to the two defects above, found while verifying manually
against the live provider:** the design docs and the original `ResilienceOptions` default both
named `https://api.frankfurter.app`. That domain carries a `Deprecation` header already past its
own date and now answers only via a 301 to `https://api.frankfurter.dev` — same
`/v1/latest?from=..&to=..` shape, different host. `ResilienceOptions.ExchangeRateBaseAddress`'s
default and `appsettings.json` were updated to the `.dev` host directly, with the reasoning kept
in the option's own remarks; the ADR and spec's mentions of the old domain were corrected too.
`.dev`'s own successor-version header points at a `/v2/rates` endpoint with a different,
undocumented parameter shape (rejected `symbols` as an unknown parameter when tried) — not
chased, since this reference integration's test suite never calls the live provider regardless
of which host is configured.

**Verify:** `dotnet test tests/Application.Tests`, then `dotnet test tests/Infrastructure.Tests`.

**Done, verified with a real Postgres container and Docker available throughout.** All four
projects pass in full: Domain 74, Application 109 (13 new `GetExchangeRateHandlerTests`),
Infrastructure 156 (34 new under `Resilience/`, including `ExchangeRateClientTests` and the new
`AddInfrastructure_WiresExchangeRateClientIn` wiring test), Api 111 (unchanged count — the query
is registered but not yet reachable over HTTP; that is Task 6). Debug and Release both build with
zero warnings; `Api.IntegrationTests` re-run clean under `-c Release`; `codegen write` produces
no diff. Every `ExchangeRateClientTests` run completes in well under one second of real time
despite simulating retries, an exhausted retry budget, and a 20-second `Retry-After` wait.

---

### Task 6: The endpoint

**Files:**
- Create: `src/Api/Rates/RatesController.cs`, `src/Api/Rates/RateDtos.cs`
- Create: `tests/Api.IntegrationTests/Rates/RatesEndpointTests.cs`
- Modify: `tests/Api.IntegrationTests/ApiFactory.cs`

**Steps:**
- [x] `services.AddQuery<GetExchangeRate, ExchangeRateView, GetExchangeRateHandler>()` in
      `AddMessaging()` — already done in Tasks 4/5, not a separate step here: registering the
      query could not wait for this task, per Tasks 4/5's own "found, not planned" note.
- [x] `GET /api/rates?from=EUR&to=USD`, `[Authorize]` (the query is `ICacheable`, and one
      dispatched with no caller throws by design — the same reason `ProductsController` gives).
- [x] `[ProducesResponseType]` for 200, 400 and **503**. (401 needs no attribute of its own —
      `[Authorize]` alone puts it in the contract, the same as every other authorized endpoint.)
- [x] DTOs use `required` + `init`, per `Api/CLAUDE.md`.
- [x] `ApiFactory`: `Resilience:Enabled=false` plus `Resilience:ExchangeRateBaseAddress` set to
      `http://127.0.0.1:1` — loopback, nothing ever listening, so the connection is refused
      immediately with no DNS lookup and no real wall-clock wait. Chosen over a hand-built stub
      HTTP server: no test in this task needs a *successful* rate, only a reliably unreachable
      one, and "refused instantly" is simpler and faster than standing up something to refuse it.
- [x] Integration tests: 401 unauthenticated; 400 on a malformed currency code (proven not to
      reach the unreachable address, since validation happens first); 400 on a missing one; 503
      with `Retry-After` when the provider is unreachable (using `ResultExtensions.Problem`'s
      Task 2 fallback floor, since the connection-refused failure carries no header of its own
      for the adapter to forward).

**Verify:** `dotnet test tests/Api.IntegrationTests`.

**Done, verified with a real Postgres container and Docker available throughout.** All 115 Api
tests pass (111 + 4 new `RatesEndpointTests`), the 503 test included, completing in well under a
second including one real (instantly-refused) loopback connection attempt. Debug and Release
both green; Api tests re-run clean under `-c Release`; `codegen write` produces no diff.

---

### Task 7: Regenerate the contract

**Steps:**
- [x] `dotnet restore src/Api` (separate first step — `dotnet msbuild` does not restore
      implicitly, and folding it into `-t:"Restore;Build;..."` fails with CS9137).
- [x] ```bash
      ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
        Wolverine__Durable=false \
        dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
      ```
      Ran clean; the background outbox poller logs a connection-refused warning against the
      placeholder connection string, exactly as CLAUDE.md says to expect — it is never opened,
      only required to be non-empty.
- [x] `npm run generate:api --prefix frontend`. Node in this container is 22.22.2, below the
      documented 24.15.0 floor — `npm install` was expected to fail outright per root
      `CLAUDE.md`, and instead succeeded (427 packages, 0 vulnerabilities) along with the
      generation script itself. Not a claim that the floor is wrong; only that this specific
      narrow operation happened not to hit whichever `npm`/Node-version interaction the floor
      documents, on this exact machine, today.
- [x] Commit both generated files: `openapi/AiFramework.Api.json` gained the `/api/rates` path
      (200/400/503) and the `ExchangeRateResponse` schema; `frontend/src/api/schema.d.ts`
      regenerated from it, mechanically, with no hand edits.
- [x] `dotnet run --project src/Api -- codegen write` — confirmed no diff, twice (before and
      after this task's changes). Nothing here touches a Wolverine handler.

**One thing this step surfaced that needed reverting, not committing:** `npm install` rewrote
`frontend/package-lock.json`, dropping `libc` metadata fields on several optional platform
packages — an artifact of running under npm 10.9.7 (bundled with this Node 22) rather than
whatever shipped with the pinned Node 24.20.0, not a real dependency change. Reverted with
`git checkout -- frontend/package-lock.json` before committing; only `schema.d.ts` was kept.

**Verify:** frontend `npm run lint` (clean, `--max-warnings 0`), `npm run build` (97 modules,
495ms), `npm test -- --run` (14 files, 55 tests, all passing — no frontend code consumes
`/api/rates` yet, so this is confirming no regression, not new coverage).

**Verify:** `git diff --stat` shows `openapi/AiFramework.Api.json` and
`frontend/src/api/schema.d.ts` carrying the new 503 and the rates path, and
`src/Api/Internal/Generated` untouched.

---

### Task 8: Documentation

**Files:**
- Create: `docs/adr/0014-retry-and-resilience-policies.md`
- Modify: `CLAUDE.md`, `src/Application/CLAUDE.md`, `src/Infrastructure/CLAUDE.md`

**Steps:**
- [ ] ADR 0014 — the decision, the consequences, and the alternatives, in the house shape.
- [ ] Root `CLAUDE.md`: a **Resilience** section beside **Caching**, carrying the three things
      that will cost someone an afternoon — Polly cannot see a failed `Result`; explicit
      transactions now need the execution strategy; retry is off under test the way the cache is.
- [ ] `src/Application/CLAUDE.md`: no resilience package in this layer, and a port returns
      `Result<T>` with the transport's failure already translated.
- [ ] `src/Infrastructure/CLAUDE.md`: a **Resilience** section — the pipeline lives under the
      port, the strategy order is not to be rearranged, and the non-GET idempotency rule.

**Verify:** `.claude/hooks/verify-build.ps1` and a read-through against the spec.

---

### Task 9: Full verification

- [ ] `/verify` — both stacks, Debug **and** Release. Release is the one that catches a
      startup-time failure, which is exactly the shape a misconfigured resilience handler has
      (`AttemptTimeout > TotalRequestTimeout` throws at build-of-pipeline time).
- [ ] `dotnet test` each of the four projects, one at a time.
- [ ] `npm run lint && npm run build && npm test --prefix frontend`.
- [ ] `./deploy/deploy.ps1` then `./deploy/e2e-k8s.ps1` — the two-replica path, which is where
      the per-process circuit breaker and the new probe timeout actually get exercised.

## Definition of done

- A transient Postgres failure during a request retries up to three times and succeeds, instead
  of returning 500.
- An unreachable third party returns **503 with `Retry-After`**, after a bounded budget, not 500
  and not a hung request.
- A 400 from a third party is **not** retried, proven by a test that counts calls.
- Backoff is asserted without a single real delay.
- `Resilience:Enabled=false` makes every pipeline a pass-through, and the test host uses it.
- No Polly or resilience package is reachable from Application or Domain.
- `openapi/AiFramework.Api.json` and `frontend/src/api/schema.d.ts` are regenerated and committed.
