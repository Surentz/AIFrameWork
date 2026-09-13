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
      `ExchangeRateBaseAddress` (`https://api.frankfurter.app`).
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
- [ ] Add `Unavailable` to `ErrorKind` with an XML comment saying what it means and, more
      importantly, what it does not: an expected, *retryable* upstream failure, not a bug.
      A bug is an exception and belongs in `GlobalExceptionHandler`'s 500 branch.
- [ ] Map it to `StatusCodes.Status503ServiceUnavailable` in `ResultExtensions.Problem`.
- [ ] Set `Retry-After` on the 503 — the first response *header* this method writes. Use
      `Math.Ceiling`, and say why in a comment: `AuthRateLimitTests` already proves that
      truncating produces `Retry-After: 0`, which sends a well-behaved client straight back in.
- [ ] Leave every other `ErrorKind` mapping untouched.

**Verify:** `dotnet test tests/Api.IntegrationTests` — a failed `Result` carrying
`ErrorKind.Unavailable` produces 503, `application/problem+json`, a `traceId`, and a
`Retry-After` strictly greater than zero.

---

### Task 3: EF transient retry, with the rules it creates

**Files:**
- Modify: `src/Infrastructure/InfrastructureRegistration.cs`
- Modify: `src/Infrastructure/Outbox/OutboxPoller.cs` (comment only)
- Modify: `k8s/base/api.yaml`, `k8s/overlays/local/secret.yaml`
- Modify: `src/Infrastructure/CLAUDE.md`

**Steps:**
- [ ] `UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3,
      maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null))`, with a comment deriving
      the 1-second cap from the readiness probe rather than stating it as taste.
- [ ] Comment above `OutboxPoller.ClaimAsync`'s raw `NpgsqlCommand`: it bypasses the execution
      strategy by construction, and that is deliberate — the next poll cycle and the
      `LeasedUntil` clause already recover a failed claim twice over.
- [ ] `k8s/base/api.yaml`: `timeoutSeconds: 5` on the readiness probe, with a comment saying
      the Kubernetes default is 1s and that `CanConnectAsync` now goes through the strategy.
- [ ] `k8s/overlays/local/secret.yaml`: add `Timeout=5` to the connection string, with a
      comment that Npgsql's 15s default is the real floor on a blackholed host, not the retry
      delay.
- [ ] `src/Infrastructure/CLAUDE.md`, under **EF rules**: explicit transactions must go through
      `CreateExecutionStrategy().ExecuteAsync(...)`; a bare `BeginTransactionAsync` now throws.
      Note there are none in `src/` today, which is why this is cheap to adopt now.
- [ ] `src/Application/Users/SignIn.cs`: one comment on `RecordSignInOutcomeAsync`'s call site
      recording that a retried `ExecuteUpdateAsync` can double-count a failure, and that
      over-counting is ADR 0008's preferred direction.

**Verify:** `dotnet test tests/Infrastructure.Tests` (Testcontainers path still green — the
strategy changes how a failure is handled, not how a success behaves), then
`dotnet test tests/Api.IntegrationTests`.

---

### Task 4: The port and the query

**Files:**
- Create: `src/Application/Abstractions/IExchangeRateProvider.cs`
- Create: `src/Application/Orders/GetExchangeRate.cs`
- Create: `tests/Application.Tests/Orders/GetExchangeRateHandlerTests.cs`

**Steps:**
- [ ] `IExchangeRateProvider.GetRateAsync(string baseCurrency, string quoteCurrency, CancellationToken)`
      returning `Task<Result<ExchangeRate>>`. No `HttpClient`, no Polly type, no EF type — the
      dependency-rule hook blocks two of those outright and the third is the whole point.
- [ ] `GetExchangeRate(string From, string To) : IQuery<ExchangeRateView>, ICacheable` with
      `CacheKey => $"{From}:{To}"` and `Duration => TimeSpan.FromMinutes(1)`. **No user id in
      the key** — the behavior prepends `ICurrentUser.Id` already.
- [ ] The handler validates its own inputs (three-letter ISO codes, upper-cased) and returns
      `ErrorKind.Validation` — queries get no validation behavior, per `Infrastructure/CLAUDE.md`.
- [ ] The handler passes the provider's failed `Result` straight through. It does not retry,
      does not translate, and does not catch.
- [ ] Tests substitute `IExchangeRateProvider` with NSubstitute: success maps through; a
      provider `Unavailable` failure surfaces unchanged; bad currency codes never reach the port.

**Verify:** `dotnet test tests/Application.Tests`.

---

### Task 5: The typed client and its pipeline

The task the whole plan exists for. Everything Polly touches lives in this file.

**Files:**
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj`
- Create: `src/Infrastructure/Resilience/ExchangeRateClient.cs`
- Modify: `src/Infrastructure/Resilience/ResilienceRegistration.cs`
- Create: `tests/Infrastructure.Tests/Resilience/StubHttpMessageHandler.cs`,
  `tests/Infrastructure.Tests/Resilience/ExchangeRateClientTests.cs`
- Modify: `tests/Infrastructure.Tests/AiFramework.Infrastructure.Tests.csproj`

**Steps:**
- [ ] `PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="10.10.0"` in
      Infrastructure only. `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 in
      `Infrastructure.Tests` only.
- [ ] `AddHttpClient<IExchangeRateProvider, ExchangeRateClient>()` configuring `BaseAddress`
      from `ResilienceOptions`, then `.AddStandardResilienceHandler()` configured from the same
      options — **guarded by `Enabled`**, so a disabled pipeline is a plain `HttpClient`.
- [ ] A comment above the handler recording the strategy order and why it is not rearranged:
      total timeout outside the retry, attempt timeout inside it.
- [ ] `ExchangeRateClient` converts only the **final** outcome to `Result<T>`:
      a non-success status or an `HttpRequestException`/`TimeoutRejectedException` escaping the
      pipeline becomes `ErrorKind.Unavailable`; a 4xx that is not 408/429 becomes
      `ErrorKind.Validation` or `NotFound` as appropriate. Catch the specific exception types,
      never `Exception` — CA1031 is an error.
- [ ] A comment in the adapter stating the rule in decision 2: nothing inside the pipeline may
      return a failed `Result`, because Polly would read it as a success.
- [ ] `ExchangeRateClientTests` drive a stub `HttpMessageHandler` with a `FakeTimeProvider`:
      a 503 then a 200 retries and succeeds; three 503s exhaust and return `Unavailable`; a 400
      is **not** retried (assert the stub was called exactly once); `Retry-After` on a 429 is
      honoured over the computed backoff; `Enabled = false` calls exactly once. No real network,
      no wall-clock delay.

**Verify:** `dotnet test tests/Infrastructure.Tests`.

---

### Task 6: The endpoint

**Files:**
- Create: `src/Api/Rates/RatesController.cs`, `src/Api/Rates/RateDtos.cs`
- Modify: `src/Infrastructure/InfrastructureRegistration.cs` (register the query)
- Create: `tests/Api.IntegrationTests/Rates/RatesEndpointTests.cs`
- Modify: `tests/Api.IntegrationTests/ApiFactory.cs`

**Steps:**
- [ ] `services.AddQuery<GetExchangeRate, ExchangeRateView, GetExchangeRateHandler>()` in
      `AddMessaging()` — the completeness test fails the build without it.
- [ ] `GET /api/rates?from=EUR&to=USD`, `[Authorize]` (the query is `ICacheable`, and one
      dispatched with no caller throws by design — the same reason `ProductsController` gives).
- [ ] `[ProducesResponseType]` for 200, 400, 401 and **503**. The 503 attribute is what puts
      the new status into the committed contract.
- [ ] DTOs use `required` + `init`, per `Api/CLAUDE.md`.
- [ ] `ApiFactory`: `Resilience:Enabled=false` and a stub base address, with a comment giving
      both reasons — a test must not sit through a backoff, and the suite must never reach the
      live provider.
- [ ] Integration tests: 401 unauthenticated; 400 on a bad currency code; 503 with
      `Retry-After` when the stubbed provider is down.

**Verify:** `dotnet test tests/Api.IntegrationTests`.

---

### Task 7: Regenerate the contract

**Steps:**
- [ ] `dotnet restore src/Api` (separate first step — `dotnet msbuild` does not restore
      implicitly, and folding it into `-t:"Restore;Build;..."` fails with CS9137).
- [ ] ```bash
      ConnectionStrings__Default='Host=localhost;Port=55433;Database=placeholder;Username=x;Password=y' \
        Wolverine__Durable=false \
        dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"
      ```
- [ ] `npm run generate:api --prefix frontend`
- [ ] Commit both generated files.
- [ ] `dotnet run --project src/Api -- codegen write` — **expect no diff.** No Wolverine handler
      changed, but CI re-runs it and fails on any, so confirm rather than assume.

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
