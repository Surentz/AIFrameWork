# E2E Test Architecture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure the Playwright suite onto fixtures and screen modules that stay cheap to write at hundreds of tests, make it runnable against the kind Kubernetes cluster as well as the local stack, and close the rate-limiter proxy defect that discovery surfaced.

**Architecture:** Playwright fixtures own lifecycle and data (one registration per *worker*, over HTTP, reused via `storageState`); plain function modules own locators and interactions per route; assertions stay in specs. One `Target` module decides base URL, whether Playwright manages the stack, and TLS handling, so the same specs run local, kind, or any URL. A configuration-gated `UseForwardedHeaders` fixes the limiter partitioning behind a proxy without opening a spoofing bypass.

**Tech Stack:** Playwright 1.62 (`@playwright/test`), TypeScript 6 (strict, `exactOptionalPropertyTypes`, `noUncheckedIndexedAccess`), Node 24 (runs `.ts` directly), .NET 10 / ASP.NET Core, xUnit + FluentAssertions, PowerShell 5.1, kind + kustomize.

**Spec:** `docs/superpowers/specs/2026-09-12-e2e-test-architecture-design.md`

## Global Constraints

- **Warnings are errors** across compiler, analyzers, and build (`Directory.Build.props`). Any suppression needs a justification comment directly above it.
- **ESLint runs with `--max-warnings 0`.** One warning fails `npm run lint`.
- **TypeScript strictness applies to `e2e/`**: `frontend/tsconfig.node.json` already includes it with `strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`, `noImplicitOverride`, `noImplicitReturns`. In particular, **`exactOptionalPropertyTypes` forbids passing an explicit `undefined` to an optional property** — use conditional object spread instead.
- **No `any`, no `!` non-null assertion** (`frontend/CLAUDE.md`).
- **Never `catch (Exception)`** in C#; `throw;` never `throw ex;`.
- **Never hand-edit an applied EF migration.** No migrations are added by this plan.
- Test naming in .NET: `MethodName_Scenario_ExpectedOutcome`. One behaviour per test.
- **No `Thread.Sleep`, no `Task.Delay`, no retry-until-timeout** in .NET tests (`tests/CLAUDE.md`).
- New `Api.IntegrationTests` classes needing the database join `[Collection(nameof(ApiFactoryCollection))]`. A class that needs a *differently configured host* stands up its own `WebApplicationFactory<Program>` instead — the split `AuthRateLimitTests` already uses.
- **Every commit message ends with:**
  ```
  Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01WaArfwR4LAikreBosVHZAM
  ```
- Domain constraints the tests depend on: `User.MaxUsernameLength` = 32; usernames allow only `[A-Za-z0-9._-]`; `PasswordPolicy.MinimumLength` = 12.
- `frontend/vite.config.ts` already excludes `e2e/**` from Vitest. **Do not narrow that exclusion** — every file this plan adds lives under `e2e/`, and Vitest's default glob would otherwise try to run Playwright specs.

---

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `frontend/e2e/support/target.ts` | Resolve which environment the run points at |
| `frontend/e2e/support/identity.ts` | Collision-free usernames and SKUs; the shared password |
| `frontend/e2e/setup/run.ts` | Single entry point: set target, prep DB if managed, spawn Playwright |
| `frontend/e2e/fixtures/api.ts` | HTTP registration and order arrange helpers |
| `frontend/e2e/fixtures/index.ts` | The extended `test`/`expect` every spec imports |
| `frontend/e2e/screens/{shell,login,register,orders,account}.ts` | Locators and interactions per route |
| `frontend/e2e/specs/auth/*.spec.ts`, `frontend/e2e/specs/orders/*.spec.ts` | The tests |
| `tests/Api.IntegrationTests/Auth/ForwardedHeadersTests.cs` | The limiter's proxy behaviour, both flag states |
| `deploy/e2e-k8s.ps1` | Run the suite against the kind cluster |
| `scripts/e2e.ps1`, `scripts/e2e-report.ps1` | Control-panel entry points for the local run and the report |
| `frontend/e2e/CLAUDE.md` | The conventions, loaded automatically in that directory |
| `docs/adr/0012-end-to-end-test-architecture.md` | The decision record |

**Moved**

| From | To |
|---|---|
| `frontend/e2e/env.ts` | `frontend/e2e/support/env.ts` |
| `frontend/e2e/prepare-database.ts` | `frontend/e2e/setup/prepare-database.ts` |
| `frontend/e2e/global-teardown.ts` | `frontend/e2e/setup/global-teardown.ts` |
| `frontend/e2e/auth.spec.ts` | `frontend/e2e/specs/auth/sign-in.spec.ts` |
| `frontend/e2e/orders.spec.ts` | `frontend/e2e/specs/orders/place-order.spec.ts` (+ `validation.spec.ts`) |

**Deleted**

| File | Why |
|---|---|
| `frontend/e2e/sign-up.ts` | Replaced by the `workerUser` / `freshUser` fixtures |

**Modified**

`frontend/playwright.config.ts`, `frontend/package.json`, `src/Api/Program.cs`,
`k8s/overlays/local/config.yaml`, `scripts/install-prereqs.ps1`,
`local-run/control-panel.bat`, `.github/workflows/ci.yml`, `frontend/CLAUDE.md`,
root `CLAUDE.md`.

---

## Task 1: Gate `UseForwardedHeaders` behind configuration

The rate limiter partitions on `Connection.RemoteIpAddress`, which behind ingress-nginx is the
ingress pod for every caller. Fixing it requires clearing proxy trust, which — if done
unconditionally — lets anyone who can reach the API directly spoof `X-Forwarded-For` and mint a
fresh partition per request. So the fix is configuration-gated and off by default, and the
**flag-off test is the regression guard** that stops the gate being removed later.

**Files:**
- Modify: `src/Api/Program.cs` (limiter is at `:130-150`, `app.UseRateLimiter()` at `:224`)
- Create: `tests/Api.IntegrationTests/Auth/ForwardedHeadersTests.cs`
- Modify: `k8s/overlays/local/config.yaml`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: configuration key `ForwardedHeaders:Enabled` (bool, default `false`); environment
  variable form `ForwardedHeaders__Enabled`. Nothing in later tasks reads it in code — Task 7's
  cluster run depends on it being set in the overlay.

- [ ] **Step 1: Write the failing tests**

Create `tests/Api.IntegrationTests/Auth/ForwardedHeadersTests.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiFramework.Api.IntegrationTests.Auth;

/// <summary>
/// Whether the auth limiter believes X-Forwarded-For. Outside ApiFactoryCollection for the same
/// reason AuthRateLimitTests is: these hosts run with a deliberately tiny permit limit, which the
/// shared factory raises out of the way.
/// </summary>
/// <remarks>
/// The flag-OFF test is the important one. Trusting the header requires clearing
/// KnownNetworks/KnownProxies, and a build that trusts it everywhere would let any caller who can
/// reach the API directly mint a fresh rate-limit partition per request by varying the header —
/// a complete bypass of ADR 0008's volume defence. If this test ever goes green by accident, the
/// gate has been removed.
/// </remarks>
public sealed class ForwardedHeadersTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const int PermitLimit = 1;
    private const int WindowSeconds = 60;

    private readonly WebApplicationFactory<Program> _bare;

    public ForwardedHeadersTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _bare = factory;
    }

    private WebApplicationFactory<Program> HostWith(bool forwardedHeadersEnabled) =>
        _bare.WithWebHostBuilder(builder =>
        {
            // No container: every request below is rejected by the validator (empty username) or
            // by the limiter, so none reaches the database. Same placeholder trick HealthTests
            // and AuthRateLimitTests use, and the same reason Wolverine durability is off.
            builder.UseSetting(
                "ConnectionStrings:Default",
                "Host=localhost;Database=placeholder;Username=placeholder;Password=placeholder");
            builder.UseSetting("Wolverine:Durable", "false");
            builder.UseSetting(
                "RateLimiting:Auth:PermitLimit",
                PermitLimit.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting(
                "RateLimiting:Auth:WindowSeconds",
                WindowSeconds.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting(
                "ForwardedHeaders:Enabled",
                forwardedHeadersEnabled ? "true" : "false");
        });

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { Username = "", Password = "", RememberMe = false }),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task PostLogin_WithForwardedHeadersDisabled_IgnoresTheHeader()
    {
        using var factory = HostWith(forwardedHeadersEnabled: false);
        using var client = factory.CreateClient();

        var first = await PostLoginAsync(client, "203.0.113.1");
        first.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "an empty username fails validation, but the request still spends a permit");

        var second = await PostLoginAsync(client, "203.0.113.2");

        second.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests,
            "with the header ignored both requests share the one partition, so the second is over the limit");
    }

    [Fact]
    public async Task PostLogin_WithForwardedHeadersEnabled_PartitionsByTheForwardedAddress()
    {
        using var factory = HostWith(forwardedHeadersEnabled: true);
        using var client = factory.CreateClient();

        var first = await PostLoginAsync(client, "203.0.113.1");
        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var second = await PostLoginAsync(client, "203.0.113.2");

        second.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a different forwarded address is a different partition, so it has its own permit");

        var third = await PostLoginAsync(client, "203.0.113.1");

        third.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests,
            "the first address has already spent its single permit");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Api.IntegrationTests --filter FullyQualifiedName~ForwardedHeadersTests`

Expected: `PostLogin_WithForwardedHeadersEnabled_PartitionsByTheForwardedAddress` FAILS — the
third assertion gets `BadRequest` where `TooManyRequests` is expected, because with no
`UseForwardedHeaders` registered every request lands in the single `"unknown"` partition and
`PermitLimit` is never reached per-address. `PostLogin_WithForwardedHeadersDisabled_IgnoresTheHeader`
should already PASS — it describes today's behaviour, and it is the guard, not the change.

- [ ] **Step 3: Register the gated middleware**

In `src/Api/Program.cs`, add to the `using` block:

```csharp
using Microsoft.AspNetCore.HttpOverrides;
```

Immediately after the `builder.Services.AddRateLimiter(...)` block (ends around `:160`), add:

```csharp
// Off by default, and that default is load-bearing. The limiter above partitions on
// Connection.RemoteIpAddress, which behind ingress-nginx is the ingress pod for every caller -
// so the whole world shares one partition. Believing X-Forwarded-For fixes that, but only by
// clearing the proxy allow-list below, and a host that trusts the header while being directly
// reachable lets any caller mint a fresh partition per request simply by varying it. Enable it
// only where an ingress is provably the sole path in; see ADR 0012.
if (builder.Configuration.GetValue("ForwardedHeaders:Enabled", defaultValue: false))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        // XForwardedFor only. Nothing in this application reads Request.IsHttps -
        // CookieSecurePolicy.Always is unconditional in Production - so forwarding the proto
        // would change behaviour for no benefit.
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;

        // Exactly one proxy: the ingress. A larger limit would let a client prepend its own
        // hop and choose which address the limiter sees.
        options.ForwardLimit = 1;

        // Cleared because the proxy is a cluster-assigned pod IP, not loopback, and its address
        // is not knowable at build time. Safe only under the flag above.
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });
}
```

Then in the pipeline, **before** `app.UseRateLimiter();` (currently `:224`), add:

```csharp
// Must precede UseRateLimiter: after it, the limiter has already read the pre-rewrite address
// and the rewrite changes nothing. Registered unconditionally - with the flag off,
// ForwardedHeadersOptions keeps its defaults, which forward nothing.
app.UseForwardedHeaders();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Api.IntegrationTests --filter FullyQualifiedName~ForwardedHeadersTests`
Expected: both PASS.

If `PostLogin_WithForwardedHeadersEnabled_PartitionsByTheForwardedAddress` still fails, the
cause is that `ForwardedHeadersMiddleware` declined to rewrite a null `RemoteIpAddress`
(`AuthRateLimitTests` documents that an in-memory test host has none). The fix is **not** to
weaken the test: add a middleware ahead of the pipeline that gives the request an address, using
the `IStartupFilter` pattern already in this project at
`tests/Api.IntegrationTests/Diagnostics/TestEndpointsStartupFilter.cs` — an `IStartupFilter`'s
`app.Use(...)` runs before the application's own pipeline, which is exactly the hook needed:

```csharp
app.Use(async (context, next) =>
{
    context.Connection.RemoteIpAddress ??= System.Net.IPAddress.Parse("198.51.100.7");
    await next(context);
});
```

- [ ] **Step 5: Run the whole backend suite for regressions**

Run: `dotnet test`
Expected: all green. `AuthRateLimitTests` in particular must still pass — it relies on the
single `"unknown"` partition, which the flag-off default preserves.

- [ ] **Step 6: Enable the flag in the cluster overlay**

In `k8s/overlays/local/config.yaml`, add below `RateLimiting__Auth__WindowSeconds`:

```yaml
  # Note the DOUBLE underscore, like every key here. Safe in this overlay specifically: the
  # ingress is the only route to the api Service, so X-Forwarded-For cannot be attacker-chosen.
  # Do not copy this into an overlay whose API is reachable directly - see ADR 0012.
  ForwardedHeaders__Enabled: 'true'
```

- [ ] **Step 7: Commit**

```bash
git add src/Api/Program.cs tests/Api.IntegrationTests/Auth/ForwardedHeadersTests.cs k8s/overlays/local/config.yaml
git commit -m "$(cat <<'EOF'
fix(api): partition the auth limiter by the real client address behind a proxy

The policy partitions on Connection.RemoteIpAddress and nothing registered
UseForwardedHeaders, so behind ingress-nginx every caller shared one partition
against the overlay's 10-per-60-seconds budget.

Gated by ForwardedHeaders:Enabled, default off: trusting the header requires
clearing the proxy allow-list, and a directly reachable host that trusts it lets
any caller mint a fresh partition per request. The flag-off test is the guard.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01WaArfwR4LAikreBosVHZAM
EOF
)"
```

---

## Task 2: Support layer, Playwright config, and the single entry point

Restructures the directory and rewires the config **without changing any test**. The existing six
tests must still pass at the end of this task — that is what proves the rewiring.

**Files:**
- Create: `frontend/e2e/support/target.ts`, `frontend/e2e/support/identity.ts`, `frontend/e2e/setup/run.ts`
- Move: `e2e/env.ts` → `e2e/support/env.ts`; `e2e/prepare-database.ts` → `e2e/setup/prepare-database.ts`; `e2e/global-teardown.ts` → `e2e/setup/global-teardown.ts`; `e2e/sign-up.ts` → `e2e/support/sign-up.ts`; `e2e/auth.spec.ts` → `e2e/specs/auth.spec.ts`; `e2e/orders.spec.ts` → `e2e/specs/orders.spec.ts`
- Modify: `frontend/playwright.config.ts`, `frontend/package.json`, `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces:
  - `resolveTarget(): Target` where `interface Target { readonly name: string; readonly baseURL: string; readonly managesStack: boolean; readonly ignoreHTTPSErrors: boolean }` — Tasks 3, 4 and the teardown all read it.
  - `uniqueUsername(prefix?: string): string`, `uniqueSku(): string`, `PASSWORD: string` — Tasks 3, 5, 6.
  - `npm run e2e` / `e2e:ui` / `e2e:kind` / `e2e:url` / `e2e:report` — Task 7 calls these.

- [ ] **Step 1: Create the directories and move the files**

```bash
cd frontend/e2e
mkdir -p support setup screens fixtures specs/auth specs/orders
git mv env.ts support/env.ts
git mv sign-up.ts support/sign-up.ts
git mv prepare-database.ts setup/prepare-database.ts
git mv global-teardown.ts setup/global-teardown.ts
git mv auth.spec.ts specs/auth.spec.ts
git mv orders.spec.ts specs/orders.spec.ts
```

Then fix the now-broken relative imports:
- `setup/prepare-database.ts`: `from './env.ts'` → `from '../support/env.ts'`
- `specs/auth.spec.ts` and `specs/orders.spec.ts`: `from './sign-up.ts'` → `from '../support/sign-up.ts'`

Leave everything else in those files alone — including `prepare-database.ts`'s
`'../docker-compose.e2e.yml'`, which is relative to the **working directory** (`frontend/`), not
to the file.

- [ ] **Step 2: Write `support/identity.ts`**

```ts
import { randomUUID } from 'node:crypto';

/**
 * A username no other test will pick. `Date.now()` is not enough once workers run in parallel:
 * two of them registering inside the same millisecond collide, and the 409 surfaces as an
 * unrelated navigation timeout.
 *
 * RegisterUserValidator allows only [A-Za-z0-9._-] and User.MaxUsernameLength is 32, so the
 * hyphen is fine and the 16-character result is comfortably inside the limit.
 */
export function uniqueUsername(prefix = 'e2e'): string {
  return `${prefix}-${randomUUID().replaceAll('-', '').slice(0, 12)}`;
}

/** The same idea for order SKUs, which tests assert on by exact text. */
export function uniqueSku(): string {
  return `SKU-E2E-${randomUUID().replaceAll('-', '').slice(0, 8).toUpperCase()}`;
}

/** PasswordPolicy.MinimumLength is 12; this is comfortably above it. */
export const PASSWORD = 'a long enough e2e password';
```

- [ ] **Step 3: Write `support/target.ts`**

```ts
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { PREVIEW_PORT } from './env.ts';

export interface Target {
  /** For log lines and failure messages. */
  readonly name: string;
  readonly baseURL: string;
  /** Does Playwright start the servers, migrate the database, and tear it down? */
  readonly managesStack: boolean;
  readonly ignoreHTTPSErrors: boolean;
}

const KindHost = 'aiframework.localtest.me';

/**
 * The ingress https port comes from deploy/kind-cluster.yaml — the same file and the same
 * mapping deploy/deploy.ps1 parses to print its "Ready:" URL. It is 8443 today only because
 * host 443 is held by http.sys on this machine; hard-coding it here would drift silently the
 * moment that changes.
 */
export function kindBaseUrl(): string {
  const configPath = fileURLToPath(new URL('../../../deploy/kind-cluster.yaml', import.meta.url));
  const yaml = readFileSync(configPath, 'utf8');
  const match = /containerPort:\s*443\s*\r?\n\s*hostPort:\s*(\d+)/m.exec(yaml);
  const port = match?.[1] ?? '443';

  return port === '443' ? `https://${KindHost}` : `https://${KindHost}:${port}`;
}

export function resolveTarget(): Target {
  const requested = process.env.E2E_TARGET ?? 'local';

  if (requested === 'local') {
    return {
      name: 'local',
      baseURL: `http://localhost:${PREVIEW_PORT}`,
      managesStack: true,
      ignoreHTTPSErrors: false,
    };
  }

  if (requested === 'kind') {
    return {
      name: 'kind',
      baseURL: kindBaseUrl(),
      managesStack: false,
      // The kind ingress serves a self-signed certificate (k8s/overlays/local/tls.yaml). Scoped
      // to this target rather than set globally, so a genuine TLS fault elsewhere still fails.
      ignoreHTTPSErrors: true,
    };
  }

  if (!/^https?:\/\//.test(requested)) {
    throw new Error(
      `E2E_TARGET must be 'local', 'kind', or an http(s) URL. Got: '${requested}'.`,
    );
  }

  return {
    name: requested,
    baseURL: requested.replace(/\/$/, ''),
    managesStack: false,
    ignoreHTTPSErrors: false,
  };
}
```

- [ ] **Step 4: Make the teardown target-aware**

Replace `setup/global-teardown.ts` entirely:

```ts
import { execFileSync } from 'node:child_process';
import { resolveTarget } from '../support/target.ts';

export default function globalTeardown(): void {
  const target = resolveTarget();

  // Dropping a database we did not create would be wrong against kind and catastrophic against
  // anything else.
  if (!target.managesStack) {
    console.log(`e2e: target '${target.name}' is not managed from here; leaving it alone.`);
    return;
  }

  // `down -v` runs even when the suite failed, which destroys exactly the evidence you want.
  // `npm run e2e:ui` sets this so the container survives between iterations.
  if (process.env.E2E_KEEP_DATABASE === '1') {
    console.log('e2e: E2E_KEEP_DATABASE is set; leaving the e2e Postgres up.');
    return;
  }

  execFileSync('docker', ['compose', '-f', '../docker-compose.e2e.yml', 'down', '-v'], {
    stdio: 'inherit',
  });
}
```

- [ ] **Step 5: Write `setup/run.ts`**

```ts
// The single entry point for every e2e run. Two jobs: decide the target, and make sure the
// database exists before Playwright starts anything.
//
// The database prep deliberately stays OUTSIDE Playwright. Playwright launches `webServer`
// processes before `globalSetup` runs, so as a global setup it arrived too late and the API
// booted against a database that did not exist — see setup/prepare-database.ts's own header.
// Chaining it with `&&` in an npm script had the opposite failure: `npx playwright test` run
// directly (an IDE, `--ui`) skipped it silently. Doing it here keeps the ordering constraint and
// removes the footgun.
//
// It also sets E2E_TARGET itself, because `E2E_TARGET=kind npm run e2e` is bash syntax that does
// nothing in PowerShell — and adding cross-env for one variable is not worth a dependency.
import { spawnSync } from 'node:child_process';

function run(command: string, args: readonly string[]): void {
  const result = spawnSync(command, [...args], { stdio: 'inherit' });

  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}

const argv = process.argv.slice(2);
const passthrough: string[] = [];
let target = 'local';

for (let i = 0; i < argv.length; i += 1) {
  const arg = argv[i];

  if (arg === '--target') {
    const value = argv[i + 1];
    if (value === undefined) {
      throw new Error("--target needs a value: 'local', 'kind', or an http(s) URL.");
    }
    target = value;
    i += 1;
    continue;
  }

  if (arg !== undefined) {
    passthrough.push(arg);
  }
}

process.env.E2E_TARGET = target;

// UI mode is for iterating on one spec; tearing the container down after every run would make
// the next iteration pay for a fresh migrate.
if (passthrough.includes('--ui')) {
  process.env.E2E_KEEP_DATABASE = '1';
}

if (target === 'local') {
  run(process.execPath, ['e2e/setup/prepare-database.ts']);
}

const playwrightArgs = ['playwright', 'test', ...passthrough];

// Anything we do not manage has production-shaped configuration: caching on, the real rate
// limit, and a database that keeps whatever earlier runs left behind.
if (target !== 'local') {
  playwrightArgs.push('--grep-invert', '@local-only');
}

// npx is a .cmd shim on Windows and spawnSync does not resolve it without the extension.
run(process.platform === 'win32' ? 'npx.cmd' : 'npx', playwrightArgs);
```

- [ ] **Step 6: Rewrite `playwright.config.ts`**

```ts
import { defineConfig, devices } from '@playwright/test';
import { API_PORT, E2E_CONNECTION_STRING, PREVIEW_PORT } from './e2e/support/env.ts';
import { resolveTarget } from './e2e/support/target.ts';

const target = resolveTarget();
const isCI = Boolean(process.env.CI);

export default defineConfig({
  testDir: './e2e/specs',
  globalTeardown: './e2e/setup/global-teardown.ts',

  fullyParallel: true,

  // With several people writing tests, a stray `test.only` silently shrinking CI to one test is
  // a matter of time.
  forbidOnly: isCI,

  retries: isCI ? 1 : 0,

  // Playwright's 5s default is tight for a cold .NET first request, more so through an ingress.
  expect: { timeout: 10_000 },
  timeout: target.managesStack ? 30_000 : 60_000,

  reporter: isCI
    ? [['list'], ['html', { open: 'never' }], ['github']]
    : [['list'], ['html', { open: 'never' }]],

  use: {
    baseURL: target.baseURL,
    ignoreHTTPSErrors: target.ignoreHTTPSErrors,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },

  // One browser today. Named anyway: it labels the report, and adding firefox/webkit becomes a
  // three-line change rather than a restructure.
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],

  // Conditional spread, not `workers: cond ? undefined : 2`: exactOptionalPropertyTypes rejects
  // an explicit undefined for an optional property. Two workers off-target keeps registrations
  // inside the cluster's 10-per-60-seconds auth budget (one per worker, see e2e/CLAUDE.md).
  ...(target.managesStack ? {} : { workers: 2 }),

  ...(target.managesStack
    ? {
        webServer: [
          {
            // --no-launch-profile, not --launch-profile http: launchSettings.json hard-codes
            // applicationUrl to 5234 and wins over ASPNETCORE_URLS, so API_PORT was silently
            // ignored and the run hung for 120s. Setting the environment explicitly keeps it honest.
            command: 'dotnet run --project ../src/Api --no-launch-profile',
            // /health already exists, so readiness is a real check rather than a fixed wait.
            // (Only true here, where this reaches the API directly. Through the kind ingress
            // /health is served by nginx — see deploy/e2e-k8s.ps1.)
            url: `http://localhost:${API_PORT}/health`,
            timeout: 120_000,
            reuseExistingServer: false,
            // Playwright discards webServer output by default, which makes a start-up failure in
            // CI unreadable: all it reports is "Process from config.webServer was not able to
            // start. Exit code: 1", naming neither which server nor why.
            stdout: 'pipe',
            stderr: 'pipe',
            env: {
              ConnectionStrings__Default: E2E_CONNECTION_STRING,
              ASPNETCORE_URLS: `http://localhost:${API_PORT}`,
              ASPNETCORE_ENVIRONMENT: 'Development',
              // The same move ApiFactory makes. No appsettings file has a RateLimiting section,
              // so this API would run on the 10-per-60-seconds default - and every spec reaches
              // it through the preview proxy, so the limiter sees one address and one partition
              // for the whole suite.
              RateLimiting__Auth__PermitLimit: '1000000',
              // The same move ApiFactory makes. A cached page would turn the eviction path into a
              // source of intermittent failures in a suite that is not testing the cache.
              Cache__Enabled: 'false',
            },
          },
          {
            command: `npm run build && npm run preview -- --port ${PREVIEW_PORT}`,
            url: `http://localhost:${PREVIEW_PORT}`,
            timeout: 120_000,
            reuseExistingServer: false,
            stdout: 'pipe',
            stderr: 'pipe',
          },
        ],
      }
    : {}),
});
```

- [ ] **Step 7: Update the npm scripts**

In `frontend/package.json`, replace the `e2e` script with:

```jsonc
"e2e": "node e2e/setup/run.ts",
"e2e:ui": "node e2e/setup/run.ts --ui",
"e2e:kind": "node e2e/setup/run.ts --target kind",
"e2e:url": "node e2e/setup/run.ts --target",
"e2e:report": "playwright show-report"
```

`e2e:url` takes the URL after npm's separator: `npm run e2e:url -- https://staging.example.com`.

- [ ] **Step 8: Verify the type-check and lint pass**

Run: `npm run build --prefix frontend && npm run lint --prefix frontend`
Expected: both succeed. `npm run build` runs `tsc -b`, which type-checks `e2e/` through
`tsconfig.node.json`.

- [ ] **Step 9: Verify target resolution for all three shapes**

Run from `frontend/`:

```bash
node --input-type=module -e "
import { resolveTarget } from './e2e/support/target.ts';
console.log('local:', JSON.stringify(resolveTarget()));
process.env.E2E_TARGET = 'kind';
console.log('kind :', JSON.stringify(resolveTarget()));
process.env.E2E_TARGET = 'https://staging.example.com/';
console.log('url  :', JSON.stringify(resolveTarget()));
process.env.E2E_TARGET = 'nonsense';
try { resolveTarget(); console.log('BUG: nonsense accepted'); }
catch (e) { console.log('rejected:', e.message); }
"
```

Expected: `local` has `managesStack: true` and port 4173; `kind` has
`baseURL: "https://aiframework.localtest.me:8443"` and `ignoreHTTPSErrors: true`; the URL case
has the trailing slash stripped; `nonsense` is rejected with the message naming the three valid
shapes.

- [ ] **Step 10: Run the existing suite unchanged**

Run: `npm run e2e --prefix frontend`
Expected: all 6 existing tests PASS. This is the proof that the restructure changed only wiring.

- [ ] **Step 11: Commit**

```bash
git add frontend/e2e frontend/playwright.config.ts frontend/package.json
git commit -m "$(cat <<'EOF'
refactor(e2e): resolve the target in one place and run through one entry point

Adds support/target.ts, so the same specs can point at the local stack, the kind
cluster, or any URL, and playwright.config.ts derives baseURL, webServer,
workers, and TLS handling from it.

Replaces the `prepare-database && playwright test` chain with setup/run.ts. The
prep stays outside Playwright - webServer starts before globalSetup, which is
why it cannot move - but `npx playwright test` can no longer skip it silently.

No test changed; the existing six still pass.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01WaArfwR4LAikreBosVHZAM
EOF
)"
```

---

## Task 3: The fixture layer

One registration per **worker**, over HTTP, reused through `storageState` — the thing that keeps
auth calls proportional to worker count rather than test count, and so keeps the suite inside the
cluster's 10-per-60-seconds budget at any size.

**Files:**
- Create: `frontend/e2e/fixtures/api.ts`, `frontend/e2e/fixtures/index.ts`

**Interfaces:**
- Consumes: `resolveTarget()` from `support/target.ts`; `PASSWORD`, `uniqueUsername`, `uniqueSku` from `support/identity.ts` (Task 2).
- Produces, all used by Tasks 4, 5, 6:
  - `interface TestUser { readonly username: string; readonly password: string; readonly state: StorageState }`
  - `registerUser(): Promise<TestUser>`
  - `interface ApiClient { register(): Promise<TestUser>; placeOrder(user: TestUser, order: { sku: string; quantity: number }): Promise<string>; placeOrders(user: TestUser, count: number): Promise<readonly string[]>; dispose(): Promise<void> }`
  - `test` and `expect` from `fixtures/index.ts`, with fixtures `signedInPage: Page`, `isolatedPage: Page`, `freshUser: TestUser`, `api: ApiClient`, and worker fixture `workerUser: TestUser`
  - `newSignedInPage(browser: Browser, user: TestUser): Promise<Page>`

- [ ] **Step 1: Write `fixtures/api.ts`**

```ts
import { request } from '@playwright/test';
import type { APIRequestContext } from '@playwright/test';
import { resolveTarget } from '../support/target.ts';
import { PASSWORD, uniqueSku, uniqueUsername } from '../support/identity.ts';

/** Whatever APIRequestContext.storageState() returns — a cookie jar, held in memory. */
export type StorageState = Awaited<ReturnType<APIRequestContext['storageState']>>;

export interface TestUser {
  readonly username: string;
  readonly password: string;
  readonly state: StorageState;
}

const target = resolveTarget();

/** The options every context in the suite is built with. */
export const connectionOptions = {
  baseURL: target.baseURL,
  ignoreHTTPSErrors: target.ignoreHTTPSErrors,
} as const;

/**
 * Registers a user over HTTP and captures the session cookie.
 *
 * Over HTTP rather than through the registration form on purpose. The form is exercised by one
 * dedicated test; doing it here too would cost seconds per test and, worse, spend one of the
 * auth endpoint's rate-limit permits per test — which against the kind cluster's
 * 10-per-60-seconds budget caps the whole suite at roughly ten tests a minute.
 */
export async function registerUser(): Promise<TestUser> {
  const context = await request.newContext(connectionOptions);

  try {
    const username = uniqueUsername();
    const response = await context.post('/api/auth/register', {
      data: { username, password: PASSWORD, displayName: 'E2E Tester' },
    });

    if (!response.ok()) {
      // Named explicitly because the interesting failure here is a 429: it means the run is
      // making more auth calls than the target's limiter allows, and left unnamed it surfaces
      // downstream as an unrelated navigation timeout.
      throw new Error(
        `Registering '${username}' failed with ${String(response.status())}: ${await response.text()}`,
      );
    }

    return { username, password: PASSWORD, state: await context.storageState() };
  } finally {
    await context.dispose();
  }
}

export interface ApiClient {
  register(): Promise<TestUser>;
  placeOrder(user: TestUser, order: { sku: string; quantity: number }): Promise<string>;
  /** `count` orders with generated SKUs, in parallel. Returns the SKUs, newest-first order not guaranteed. */
  placeOrders(user: TestUser, count: number): Promise<readonly string[]>;
  dispose(): Promise<void>;
}

/**
 * Arrange-through-the-API, so a test that needs existing data pays milliseconds instead of a
 * form-fill per row. /api/orders carries no [EnableRateLimiting] — only the auth endpoints do —
 * so this is free even against the cluster.
 */
export function createApiClient(): ApiClient {
  const contexts = new Map<string, Promise<APIRequestContext>>();

  function contextFor(user: TestUser): Promise<APIRequestContext> {
    const existing = contexts.get(user.username);
    if (existing !== undefined) {
      return existing;
    }

    const created = request.newContext({ ...connectionOptions, storageState: user.state });
    contexts.set(user.username, created);
    return created;
  }

  async function placeOrder(
    user: TestUser,
    order: { sku: string; quantity: number },
  ): Promise<string> {
    const context = await contextFor(user);
    const response = await context.post('/api/orders', { data: order });

    if (!response.ok()) {
      throw new Error(
        `Placing '${order.sku}' failed with ${String(response.status())}: ${await response.text()}`,
      );
    }

    return (await response.json()) as string;
  }

  return {
    register: registerUser,
    placeOrder,
    async placeOrders(user, count) {
      const skus = Array.from({ length: count }, () => uniqueSku());
      await Promise.all(skus.map((sku) => placeOrder(user, { sku, quantity: 1 })));
      return skus;
    },
    async dispose() {
      await Promise.all([...contexts.values()].map(async (c) => (await c).dispose()));
      contexts.clear();
    },
  };
}
```

- [ ] **Step 2: Write `fixtures/index.ts`**

```ts
/* eslint-disable no-empty-pattern -- Playwright decides which fixtures to inject by parsing the
   destructuring pattern of a fixture's first parameter, so one that depends on nothing must
   still write `{}`. Replacing it with a named parameter changes what Playwright injects. */
import { test as base } from '@playwright/test';
import type { Browser, Page } from '@playwright/test';
import { connectionOptions, createApiClient, registerUser } from './api.ts';
import type { ApiClient, TestUser } from './api.ts';

export type { TestUser } from './api.ts';

/**
 * A page signed in as `user`, in a context of its own.
 *
 * The context options are passed explicitly because a context built from `browser` directly does
 * not inherit the config's `use` block the way the built-in `page` fixture does — without this,
 * relative goto()s have no base URL and the kind run fails its TLS handshake.
 */
export async function newSignedInPage(browser: Browser, user: TestUser): Promise<Page> {
  const context = await browser.newContext({ ...connectionOptions, storageState: user.state });
  return context.newPage();
}

interface WorkerFixtures {
  workerUser: TestUser;
}

interface TestFixtures {
  api: ApiClient;
  freshUser: TestUser;
  signedInPage: Page;
  isolatedPage: Page;
}

export const test = base.extend<TestFixtures, WorkerFixtures>({
  /**
   * ONE registration for every test this worker runs. Auth calls therefore scale with worker
   * count, not test count — which is what keeps a large suite inside the real rate limit.
   *
   * The cost is that its data accumulates: never assert a list is empty or has an exact length
   * against this user. Take `freshUser` when a test needs a clean slate.
   */
  workerUser: [
    async ({}, use) => {
      await use(await registerUser());
    },
    { scope: 'worker' },
  ],

  /**
   * A user nobody else touches. Required — not merely preferred — for anything that rotates the
   * security stamp: changing a password, signing out everywhere, or tripping the lockout. Those
   * invalidate every cookie for that user, so doing them to `workerUser` breaks every later test
   * on the worker.
   */
  freshUser: async ({}, use) => {
    await use(await registerUser());
  },

  api: async ({}, use) => {
    const client = createApiClient();
    await use(client);
    await client.dispose();
  },

  /** The default for a signed-in test. */
  signedInPage: async ({ browser, workerUser }, use) => {
    const page = await newSignedInPage(browser, workerUser);
    await use(page);
    await page.context().close();
  },

  /** For the session-invalidating tests described on `freshUser`. */
  isolatedPage: async ({ browser, freshUser }, use) => {
    const page = await newSignedInPage(browser, freshUser);
    await use(page);
    await page.context().close();
  },
});

export { expect } from '@playwright/test';
```

- [ ] **Step 3: Write a throwaway spec that exercises every fixture**

Create `frontend/e2e/specs/fixtures-smoke.spec.ts` — deleted in Step 6, it exists only to prove
the wiring before any real test depends on it:

```ts
import { expect, test } from '../fixtures/index.ts';
import { uniqueSku } from '../support/identity.ts';

test('signedInPage lands on the orders page', async ({ signedInPage }) => {
  await signedInPage.goto('/orders');
  await expect(signedInPage.getByRole('heading', { name: 'Orders' })).toBeVisible();
});

test('isolatedPage is a different user from workerUser', async ({
  isolatedPage,
  freshUser,
  workerUser,
}) => {
  expect(freshUser.username).not.toBe(workerUser.username);
  await isolatedPage.goto('/orders');
  await expect(isolatedPage.getByRole('heading', { name: 'Orders' })).toBeVisible();
});

test('api.placeOrder arranges an order the UI then shows', async ({
  signedInPage,
  api,
  workerUser,
}) => {
  const sku = uniqueSku();
  await api.placeOrder(workerUser, { sku, quantity: 2 });

  await signedInPage.goto('/orders');
  await expect(signedInPage.getByRole('link', { name: sku })).toBeVisible();
});
```

- [ ] **Step 4: Run it**

Run: `npm run e2e --prefix frontend -- fixtures-smoke`
Expected: 3 PASS. If `signedInPage` lands on `/login` instead, the storage state is not reaching
the context — check that `newSignedInPage` spreads `connectionOptions` **before** `storageState`.

- [ ] **Step 5: Verify type-check and lint**

Run: `npm run build --prefix frontend && npm run lint --prefix frontend`
Expected: both succeed.

- [ ] **Step 6: Delete the smoke spec and commit**

```bash
rm frontend/e2e/specs/fixtures-smoke.spec.ts
git add frontend/e2e/fixtures
git commit -m "$(cat <<'EOF'
feat(e2e): register once per worker and reuse the session

Adds the fixture layer: workerUser registers over HTTP once per worker and every
signedInPage reuses its cookie, so auth calls scale with worker count rather
than test count - the only thing that keeps the suite inside the cluster's
10-per-60-seconds budget as it grows.

freshUser/isolatedPage exist for the tests that rotate the security stamp, which
would otherwise invalidate the shared worker cookie mid-run.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01WaArfwR4LAikreBosVHZAM
EOF
)"
```

---

## Task 4: Screen modules, and porting the existing tests

**Files:**
- Create: `frontend/e2e/screens/shell.ts`, `login.ts`, `register.ts`, `orders.ts`, `account.ts`
- Create: `frontend/e2e/specs/auth/sign-in.spec.ts`, `frontend/e2e/specs/orders/place-order.spec.ts`, `frontend/e2e/specs/orders/validation.spec.ts`
- Delete: `frontend/e2e/specs/auth.spec.ts`, `frontend/e2e/specs/orders.spec.ts`, `frontend/e2e/support/sign-up.ts`

**Interfaces:**
- Consumes: `test`, `expect`, fixtures from Task 3; `uniqueSku`, `PASSWORD` from Task 2.
- Produces, used by Tasks 5 and 6:
  - `shell.ts`: `signOutButton(p)`, `signOut(p)`
  - `login.ts`: `usernameField(p)`, `passwordField(p)`, `submitButton(p)`, `heading(p)`, `alert(p)`, `signIn(p, { username, password })`
  - `register.ts`: `usernameField(p)`, `displayNameField(p)`, `passwordField(p)`, `submitButton(p)`, `alert(p)`, `submit(p, { username, password?, displayName? })`
  - `orders.ts`: `pageHeading(p)`, `skuField(p)`, `quantityField(p)`, `submitButton(p)`, `orderLink(p, sku)`, `detailHeading(p, sku)`, `fact(p, term)`, `placeOrder(p, { sku, quantity })`
  - `account.ts`: `currentPasswordField(p)`, `newPasswordField(p)`, `changeButton(p)`, `successAlert(p)`, `alert(p)`, `signOutEverywhereButton(p)`, `changePassword(p, { current, next })`

- [ ] **Step 1: Write `screens/shell.ts`**

```ts
import type { Locator, Page } from '@playwright/test';

// exact: true matters. Playwright matches an accessible name as a substring by default, and
// /account/password also has a "Sign out everywhere" button - without this, the locator resolves
// to two elements there and fails on strict mode.
export const signOutButton = (p: Page): Locator =>
  p.getByRole('button', { name: 'Sign out', exact: true });

export const allOrdersLink = (p: Page): Locator => p.getByRole('link', { name: 'All orders' });
export const passwordLink = (p: Page): Locator => p.getByRole('link', { name: 'Password' });

export async function signOut(p: Page): Promise<void> {
  await signOutButton(p).click();
  await p.waitForURL('**/login');
}
```

- [ ] **Step 2: Write `screens/login.ts`**

```ts
import type { Locator, Page } from '@playwright/test';

export const heading = (p: Page): Locator => p.getByRole('heading', { name: 'Welcome back' });
export const usernameField = (p: Page): Locator => p.getByLabel('Username');
// exact: true - a substring match would also hit any "... password" label on the page.
export const passwordField = (p: Page): Locator => p.getByLabel('Password', { exact: true });
export const submitButton = (p: Page): Locator => p.getByRole('button', { name: 'Sign in' });
export const alert = (p: Page): Locator => p.getByRole('alert');

export async function signIn(
  p: Page,
  credentials: { username: string; password: string },
): Promise<void> {
  await p.goto('/login');
  await usernameField(p).fill(credentials.username);
  await passwordField(p).fill(credentials.password);
  await submitButton(p).click();
}
```

- [ ] **Step 3: Write `screens/register.ts`**

```ts
import type { Locator, Page } from '@playwright/test';
import { PASSWORD } from '../support/identity.ts';

export const usernameField = (p: Page): Locator => p.getByLabel('Username');
export const displayNameField = (p: Page): Locator => p.getByLabel('Display name');
export const passwordField = (p: Page): Locator => p.getByLabel('Password', { exact: true });
export const submitButton = (p: Page): Locator => p.getByRole('button', { name: 'Create account' });
export const alert = (p: Page): Locator => p.getByRole('alert');

/** Fills and submits the form. Does not wait for navigation - the caller decides what success means. */
export async function submit(
  p: Page,
  account: { username: string; password?: string; displayName?: string },
): Promise<void> {
  await p.goto('/register');
  await usernameField(p).fill(account.username);
  await displayNameField(p).fill(account.displayName ?? 'E2E Tester');
  await passwordField(p).fill(account.password ?? PASSWORD);
  await submitButton(p).click();
}
```

- [ ] **Step 4: Write `screens/orders.ts`**

```ts
import type { Locator, Page } from '@playwright/test';

export const pageHeading = (p: Page): Locator => p.getByRole('heading', { name: 'Orders' });
export const skuField = (p: Page): Locator => p.getByLabel('Sku');
export const quantityField = (p: Page): Locator => p.getByLabel('Quantity');
export const submitButton = (p: Page): Locator => p.getByRole('button', { name: 'Place order' });
export const alert = (p: Page): Locator => p.getByRole('alert');

/** A row's link in the list. */
export const orderLink = (p: Page, sku: string): Locator => p.getByRole('link', { name: sku });

/** The detail page's <h1>, which is the sku itself. */
export const detailHeading = (p: Page, sku: string): Locator =>
  p.getByRole('heading', { name: sku });

/**
 * The <dd> paired with a <dt> in the detail page's fact list, whose markup is
 * <div><dt>Quantity</dt><dd>3</dd></div>. There is no role that distinguishes one pair from
 * another, so this pairs them structurally rather than asserting on document order.
 */
export const fact = (p: Page, term: string): Locator =>
  p.locator('.order-facts > div').filter({ has: p.getByText(term, { exact: true }) }).locator('dd');

export async function placeOrder(
  p: Page,
  order: { sku: string; quantity: number },
): Promise<void> {
  await p.goto('/orders/new');
  await skuField(p).fill(order.sku);
  await quantityField(p).fill(String(order.quantity));
  await submitButton(p).click();
}
```

- [ ] **Step 5: Write `screens/account.ts`**

```ts
import type { Locator, Page } from '@playwright/test';

export const currentPasswordField = (p: Page): Locator => p.getByLabel('Current password');
export const newPasswordField = (p: Page): Locator => p.getByLabel('New password');
export const changeButton = (p: Page): Locator =>
  p.getByRole('button', { name: 'Change password' });
export const successAlert = (p: Page): Locator => p.getByRole('status');
export const alert = (p: Page): Locator => p.getByRole('alert');
export const signOutEverywhereButton = (p: Page): Locator =>
  p.getByRole('button', { name: 'Sign out everywhere' });

export async function changePassword(
  p: Page,
  passwords: { current: string; next: string },
): Promise<void> {
  await p.goto('/account/password');
  await currentPasswordField(p).fill(passwords.current);
  await newPasswordField(p).fill(passwords.next);
  await changeButton(p).click();
}
```

- [ ] **Step 6: Write `specs/auth/sign-in.spec.ts`**

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as login from '../../screens/login.ts';
import * as orders from '../../screens/orders.ts';
import * as shell from '../../screens/shell.ts';

test('signs out and back in', async ({ signedInPage, workerUser }) => {
  await signedInPage.goto('/orders');
  await shell.signOut(signedInPage);

  await login.usernameField(signedInPage).fill(workerUser.username);
  await login.passwordField(signedInPage).fill(workerUser.password);
  await login.submitButton(signedInPage).click();

  await expect(orders.pageHeading(signedInPage)).toBeVisible();
});

test('sends an anonymous visitor to the login page', async ({ page }) => {
  await page.goto('/orders');

  // The guard is a convenience; the real refusal is [Authorize] answering 401. This proves the
  // two agree - the visitor lands somewhere useful rather than on a page full of failed requests.
  await expect(page).toHaveURL(/\/login$/);
  await expect(login.heading(page)).toBeVisible();
});

test('keeps the session across a reload', async ({ signedInPage }) => {
  await signedInPage.goto('/orders');
  await signedInPage.reload();

  // The cookie is what survives here; nothing is kept in memory or localStorage.
  await expect(orders.pageHeading(signedInPage)).toBeVisible();
});

// One deliberate failed sign-in against the shared worker user. User.MaxFailedSignInAttempts is
// 5, and the fifth locks the account AND rotates the security stamp - which would sign out every
// other test on this worker. One failure (two, if CI retries this test) is well inside that, but
// it is a budget: a second failed-login test must take `freshUser` rather than spend more of it.
test('refuses a wrong password without saying whether the account exists', async ({
  page,
  workerUser,
}) => {
  await login.signIn(page, { username: workerUser.username, password: 'not the right password' });

  await expect(login.alert(page)).toHaveText(/do not match/i);
});
```

Note the last test takes the anonymous `page`, not `signedInPage`: it is about a failed sign-in,
and a failed sign-in does not disturb `workerUser`'s existing session.

- [ ] **Step 7: Write the two orders specs**

`specs/orders/place-order.spec.ts`:

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as orders from '../../screens/orders.ts';
import { uniqueSku } from '../../support/identity.ts';

// Deliberately untagged, so it also runs against the kind cluster, where Cache__Enabled is
// 'true' and there are two API replicas. That makes it an end-to-end check of ADR 0010's claim
// that ingress cookie affinity keeps L1 eviction correct - the write and the read have to land
// on the same pod for the new order to appear.
test('places an order and sees it in the list', async ({ signedInPage }) => {
  const sku = uniqueSku();

  await orders.placeOrder(signedInPage, { sku, quantity: 3 });
  await expect(orders.detailHeading(signedInPage, sku)).toBeVisible();

  await signedInPage.goto('/orders');
  await expect(orders.orderLink(signedInPage, sku)).toBeVisible();
});
```

`specs/orders/validation.spec.ts`:

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as orders from '../../screens/orders.ts';

test('shows the server validation message for an invalid quantity', async ({ signedInPage }) => {
  await orders.placeOrder(signedInPage, { sku: 'SKU-E2E-INVALID', quantity: 0 });

  // Not /quantity/i - that matches the permanent <label>Quantity</label> and would pass whether
  // or not the server ever rejected anything. RuleFor(c => c.Quantity).GreaterThan(0) produces
  // "'Quantity' must be greater than '0'.", which appears only on failure.
  await expect(signedInPage.getByText(/must be greater than/i)).toBeVisible();
});
```

- [ ] **Step 8: Delete the superseded files**

```bash
git rm frontend/e2e/specs/auth.spec.ts frontend/e2e/specs/orders.spec.ts frontend/e2e/support/sign-up.ts
```

- [ ] **Step 9: Run the suite**

Run: `npm run e2e --prefix frontend`
Expected: 6 PASS, same behaviours as before, now in `specs/auth/` and `specs/orders/`.

- [ ] **Step 10: Verify type-check and lint, then commit**

Run: `npm run build --prefix frontend && npm run lint --prefix frontend`

```bash
git add frontend/e2e
git commit -m "$(cat <<'EOF'
refactor(e2e): put locators in screen modules and port the existing specs

Locators and interactions move into per-route modules so a label change is one
edit rather than a repo-wide find-and-replace; assertions stay in the specs.

Replaces sign-up.ts, whose per-test UI registration the worker fixture now
covers. Behaviour is unchanged - the same six tests, the same assertions.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01WaArfwR4LAikreBosVHZAM
EOF
)"
```

---

## Task 5: The new auth specs

Three flows with no e2e coverage today. Two of them are untestable at any lower layer.

**Files:**
- Create: `frontend/e2e/specs/auth/registration.spec.ts`, `change-password.spec.ts`, `sign-out-everywhere.spec.ts`

**Interfaces:**
- Consumes: everything produced by Tasks 3 and 4.
- Produces: nothing other tasks depend on.

- [ ] **Step 1: Write `specs/auth/registration.spec.ts`**

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as orders from '../../screens/orders.ts';
import * as register from '../../screens/register.ts';
import { uniqueUsername } from '../../support/identity.ts';

// The only test that drives the registration form. Every other test registers over HTTP, so
// without this one the form would have no coverage at all.
test('registers a new account through the form', async ({ page }) => {
  await register.submit(page, { username: uniqueUsername() });

  // Registration signs in and lands on /orders; waiting for that is what makes this a real
  // session rather than a race.
  await page.waitForURL('**/orders');
  await expect(orders.pageHeading(page)).toBeVisible();
});

test('refuses a username that is already taken', async ({ page, api }) => {
  const existing = await api.register();

  await register.submit(page, { username: existing.username });

  // RegisterUserHandler answers 409 with "The username '<name>' is already taken."; the SPA puts
  // a ProblemDetails message with no field errors into the page-level alert.
  await expect(register.alert(page)).toHaveText(/already taken/i);
  await expect(page).toHaveURL(/\/register$/);
});
```

- [ ] **Step 2: Write `specs/auth/change-password.spec.ts`**

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as account from '../../screens/account.ts';
import * as login from '../../screens/login.ts';
import * as orders from '../../screens/orders.ts';
import * as shell from '../../screens/shell.ts';

const NewPassword = 'a different long enough password';

// isolatedPage, not signedInPage: changing a password rotates User.SecurityStamp, which
// invalidates every cookie already issued for that user. Doing it to the worker's shared user
// would sign out every later test on this worker.
test('changes the password and keeps the caller signed in', async ({ isolatedPage, freshUser }) => {
  await account.changePassword(isolatedPage, {
    current: freshUser.password,
    next: NewPassword,
  });

  await expect(account.successAlert(isolatedPage)).toHaveText(/has been changed/i);

  // The regression this test exists for. ChangePassword rotates the stamp, so without
  // AuthController re-issuing this caller's cookie, the very next request 401s and changing your
  // own password signs you out. Nothing below e2e catches it: it needs a real browser holding a
  // real cookie.
  await isolatedPage.goto('/orders');
  await expect(orders.pageHeading(isolatedPage)).toBeVisible();
});

test('accepts the new password and refuses the old one', async ({ isolatedPage, freshUser }) => {
  await account.changePassword(isolatedPage, {
    current: freshUser.password,
    next: NewPassword,
  });
  await expect(account.successAlert(isolatedPage)).toBeVisible();

  await isolatedPage.goto('/orders');
  await shell.signOut(isolatedPage);

  await login.signIn(isolatedPage, {
    username: freshUser.username,
    password: freshUser.password,
  });
  await expect(login.alert(isolatedPage)).toHaveText(/do not match/i);

  await login.signIn(isolatedPage, { username: freshUser.username, password: NewPassword });
  await expect(orders.pageHeading(isolatedPage)).toBeVisible();
});
```

- [ ] **Step 3: Write `specs/auth/sign-out-everywhere.spec.ts`**

```ts
import { expect, newSignedInPage, test } from '../../fixtures/index.ts';
import * as account from '../../screens/account.ts';
import * as login from '../../screens/login.ts';
import * as orders from '../../screens/orders.ts';

// Two independent cookie jars for one user. There is no lower layer that can test this: it is
// the whole point of the endpoint that a session OTHER than the caller's stops working.
test('ends another browser session', async ({ browser, freshUser }) => {
  const first = await newSignedInPage(browser, freshUser);
  const second = await newSignedInPage(browser, freshUser);

  await second.goto('/orders');
  await expect(orders.pageHeading(second)).toBeVisible();

  await first.goto('/account/password');

  // Waiting on the response rather than the click: the rotation has to have been committed
  // before the second session navigates, or the test races the request.
  await Promise.all([
    first.waitForResponse(
      (response) =>
        response.url().includes('/api/auth/sign-out-everywhere') && response.status() === 204,
    ),
    account.signOutEverywhereButton(first).click(),
  ]);

  await second.reload();

  // The stamp rotated, so the cookie this session holds no longer validates and RequireAuth
  // sends it to the login page.
  await expect(second).toHaveURL(/\/login$/);
  await expect(login.heading(second)).toBeVisible();

  await first.context().close();
  await second.context().close();
});
```

- [ ] **Step 4: Run the new specs**

Run: `npm run e2e --prefix frontend -- specs/auth`
Expected: 9 PASS — 4 in `sign-in.spec.ts`, 2 in `registration.spec.ts`, 2 in
`change-password.spec.ts`, 1 in `sign-out-everywhere.spec.ts`.

- [ ] **Step 5: Run the whole suite, type-check, and lint**

Run: `npm run e2e --prefix frontend && npm run build --prefix frontend && npm run lint --prefix frontend`
Expected: 11 tests PASS (9 auth + 2 orders); build and lint clean.

- [ ] **Step 6: Commit**

```bash
git add frontend/e2e/specs/auth
git commit -m "$(cat <<'EOF'
test(e2e): cover registration, password change, and sign-out-everywhere

Two of these are untestable below e2e. Changing your own password re-issues the
caller's cookie, so only a real browser holding a real cookie proves you are not
signed out by it; sign-out-everywhere needs two independent cookie jars.

All three take isolatedPage/freshUser - they rotate the security stamp, which
would invalidate the worker's shared session mid-run.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01WaArfwR4LAikreBosVHZAM
EOF
)"
```

---

## Task 6: Order detail, arranged through the API

Demonstrates the arrange-via-API pattern the conventions doc will point at.

**Files:**
- Create: `frontend/e2e/specs/orders/order-detail.spec.ts`

**Interfaces:**
- Consumes: `api.placeOrder`, `signedInPage`, `workerUser` (Task 3); `orders.*` screens (Task 4).
- Produces: nothing other tasks depend on.

- [ ] **Step 1: Write `specs/orders/order-detail.spec.ts`**

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as orders from '../../screens/orders.ts';
import { uniqueSku } from '../../support/identity.ts';

// Arranged over HTTP rather than through the form: this test is about the detail page, and
// driving the place-order form to get there would make it fail for reasons that belong to
// place-order.spec.ts.
test('opens an order from the list', async ({ signedInPage, api, workerUser }) => {
  const sku = uniqueSku();
  await api.placeOrder(workerUser, { sku, quantity: 7 });

  await signedInPage.goto('/orders');
  await orders.orderLink(signedInPage, sku).click();

  await expect(orders.detailHeading(signedInPage, sku)).toBeVisible();
  await expect(orders.fact(signedInPage, 'Quantity')).toHaveText('7');
});
```

- [ ] **Step 2: Run it**

Run: `npm run e2e --prefix frontend -- specs/orders`
Expected: 3 PASS.

If `orders.fact` resolves zero elements, the `.order-facts > div` structure in
`frontend/src/features/orders/OrderDetail.tsx` has changed — fix the locator in
`screens/orders.ts`, not the assertion.

- [ ] **Step 3: Run the whole suite and commit**

Run: `npm run e2e --prefix frontend`
Expected: 12 PASS — 9 auth, 3 orders.

```bash
git add frontend/e2e/specs/orders/order-detail.spec.ts
git commit -m "$(cat <<'EOF'
test(e2e): cover the order detail page

Arranged through the API rather than the place-order form, so a failure here
means the detail page is broken and nothing else. /api/orders carries no rate
limiting, so this pattern stays free against the cluster too.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01WaArfwR4LAikreBosVHZAM
EOF
)"
```

---

## Task 7: The Kubernetes run, the control panel, and the prerequisite

**Files:**
- Create: `deploy/e2e-k8s.ps1`, `scripts/e2e.ps1`, `scripts/e2e-report.ps1`
- Modify: `local-run/control-panel.bat`, `scripts/install-prereqs.ps1`

**Interfaces:**
- Consumes: `npm run e2e` and `node e2e/setup/run.ts --target kind` (Task 2); `ForwardedHeaders__Enabled` in the overlay (Task 1).
- Produces: menu options 6, 7, 8 in the control panel.

- [ ] **Step 1: Write `deploy/e2e-k8s.ps1`**

```powershell
#requires -Version 5.1
<#
.SYNOPSIS
  Runs the Playwright e2e suite against the local kind cluster.
.DESCRIPTION
  An additional gate, not the everyday loop: the cluster runs durable Wolverine, caching on, two
  API replicas behind cookie affinity, and the real rate limit, none of which the compose stack
  exercises.

  It assumes the cluster is already deployed and fails loudly if it is not, rather than silently
  triggering three docker builds and a rollout. Run ./deploy/start-cluster.ps1 for that.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PlaywrightArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$frontend = Join-Path $repoRoot 'frontend'

# The same mapping deploy.ps1 parses to print its "Ready:" URL. Reading it rather than hard-coding
# 8443 keeps the two in step if host 443 ever frees up.
$kindConfig = Get-Content (Join-Path $PSScriptRoot 'kind-cluster.yaml') -Raw
$httpsHostPort = if ($kindConfig -match '(?ms)containerPort:\s*443\s*\r?\n\s*hostPort:\s*(\d+)') {
    $matches[1]
}
else {
    443
}
$baseUrl = if ($httpsHostPort -eq 443) {
    'https://aiframework.localtest.me'
}
else {
    "https://aiframework.localtest.me:$httpsHostPort"
}

# Windows PowerShell 5.1 has no -SkipCertificateCheck, and the ingress serves a self-signed
# certificate (k8s/overlays/local/tls.yaml). Relaxed for this process only.
if (-not ('E2ECertPolicy' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Net;
public static class E2ECertPolicy {
    public static void Trust() {
        ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
    }
}
'@
}
[E2ECertPolicy]::Trust()

# NOT /health. The ingress routes / to the web pod, and frontend/nginx.conf ends in
# `try_files $uri $uri/ /index.html`, so /health answers 200 with the SPA whether or not the API
# is alive - a readiness gate on it is a guaranteed false positive. /api/auth/me is routed to the
# API and answers 401 when anonymous, which proves both that the API is up and that ingress
# routing works.
Write-Host "==> Checking the cluster at $baseUrl" -ForegroundColor Cyan
$status = $null
try {
    $response = Invoke-WebRequest -Uri "$baseUrl/api/auth/me" -UseBasicParsing -TimeoutSec 15
    $status = [int]$response.StatusCode
}
catch [System.Net.WebException] {
    # A 401 is the expected answer and Windows PowerShell raises it as an exception.
    if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
}

if ($status -ne 401) {
    $seen = if ($status) { "HTTP $status" } else { 'no response' }
    throw "The cluster did not answer 401 at $baseUrl/api/auth/me (got: $seen). " +
          'Deploy it first with ./deploy/start-cluster.ps1, then re-run this.'
}

Write-Host '==> Running the e2e suite against kind' -ForegroundColor Cyan
Push-Location $frontend
try {
    # Through run.ts, the same entry point every other e2e run uses, so the target handling and
    # the @local-only exclusion live in exactly one place.
    node e2e/setup/run.ts --target kind @PlaywrightArgs
    if ($LASTEXITCODE -ne 0) { throw "Playwright exited with code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'e2e against Kubernetes passed.' -ForegroundColor Green
```

- [ ] **Step 2: Write `scripts/e2e.ps1` and `scripts/e2e-report.ps1`**

`scripts/e2e.ps1`:

```powershell
#requires -Version 5.1
<#
.SYNOPSIS
  Runs the Playwright e2e suite against a stack it starts itself.
.DESCRIPTION
  A thin wrapper over `npm run e2e`, so the npm scripts stay the source of truth - the same
  relationship scripts/dev.ps1 has with the dev loop. Exists so local-run/control-panel.bat can
  offer the run without encoding any logic of its own.

  This starts its own API on 5234. Stop the dev loop first (scripts/stop-dev.ps1) or set
  API_PORT, or the two collide.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PlaywrightArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$frontend = Join-Path (Split-Path -Parent $PSScriptRoot) 'frontend'

Push-Location $frontend
try {
    npm run e2e -- @PlaywrightArgs
    if ($LASTEXITCODE -ne 0) { throw "The e2e suite failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'e2e passed.' -ForegroundColor Green
```

`scripts/e2e-report.ps1`:

```powershell
#requires -Version 5.1
<#
.SYNOPSIS
  Opens the HTML report from the last Playwright run.
.DESCRIPTION
  Separate from the run scripts because the report is worth reopening after the window that
  produced it has been closed - the normal case for someone driving this from the control panel.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$frontend = Join-Path (Split-Path -Parent $PSScriptRoot) 'frontend'
$report = Join-Path $frontend 'playwright-report/index.html'

if (-not (Test-Path $report)) {
    Write-Host 'No e2e report found. Run the suite first (control panel option 6 or 7).' -ForegroundColor DarkGray
    exit 0
}

Push-Location $frontend
try {
    npm run e2e:report
}
finally {
    Pop-Location
}
```

- [ ] **Step 3: Add the Playwright browser check to `scripts/install-prereqs.ps1`**

Insert before the `# --- npm platform override sanity check ---` section (around `:161`):

```powershell
# --- Playwright browser -----------------------------------------------------------------------
# `npm ci` installs @playwright/test but not the chromium binary it drives, so the first
# `npm run e2e` on a new machine fails with an error naming neither the cause nor the fix -
# exactly the experience the control panel exists to prevent. Unlike every other tool here this
# one is not on PATH (it lands in the user's Playwright cache), so the check delegates to
# Playwright itself, whose installer is a no-op when the browser is already present. That keeps
# this script's rule intact: install what is absent, never silently upgrade what is there.
Write-Host '==> Checking Playwright browser' -ForegroundColor Cyan
$frontendDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'frontend'
if (-not (Test-Path (Join-Path $frontendDir 'node_modules'))) {
    Add-Result -Tool 'Playwright chromium' -Status 'SKIPPED' `
        -Detail 'frontend/node_modules is missing. Run `npm ci --prefix frontend`, then re-run this script.'
}
else {
    npx --prefix $frontendDir playwright install chromium
    if ($LASTEXITCODE -eq 0) {
        Add-Result -Tool 'Playwright chromium' -Status 'OK' -Detail 'installed or already present'
    }
    else {
        Add-Result -Tool 'Playwright chromium' -Status 'FAILED' `
            -Detail "npx playwright install exited $LASTEXITCODE"
    }
}
```

Before writing this, open `scripts/install-prereqs.ps1` and confirm `Add-Result`'s parameter
names are `-Tool`, `-Status`, `-Detail`; match whatever it actually declares.

- [ ] **Step 4: Add the menu options to `local-run/control-panel.bat`**

Replace the menu block and dispatch chain. The echo list becomes:

```bat
echo   1. Install/check prerequisites (Docker, .NET SDK, Node.js, kind, k9s, Playwright)
echo   2. Start dev loop        (dev Postgres + API + Vite, scripts\dev.ps1)
echo   3. Stop dev loop         (scripts\stop-dev.ps1)
echo   4. Start Kubernetes      (creates the kind cluster if missing, else redeploys)
echo   5. Stop Kubernetes       (deletes the kind cluster - Postgres data goes with it)
echo   6. Run e2e tests         (local stack - stop the dev loop first, it uses port 5234)
echo   7. Run e2e tests         (against Kubernetes - deploy it first with option 4)
echo   8. Open last e2e report
echo   9. Exit
```

Update the prompt to `(1-9)` and extend the dispatch:

```bat
if "%choice%"=="6" goto run_e2e
if "%choice%"=="7" goto run_e2e_k8s
if "%choice%"=="8" goto open_report
if "%choice%"=="9" goto end
```

Add the labels beside the existing ones, before `:done`:

```bat
:run_e2e
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\e2e.ps1"
goto done

:run_e2e_k8s
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\deploy\e2e-k8s.ps1"
goto done

:open_report
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\e2e-report.ps1"
goto done
```

- [ ] **Step 5: Verify the local script**

Run: `./scripts/e2e.ps1`
Expected: 12 tests PASS. If port 5234 is busy, stop the dev loop first — the message from
`dev.ps1`'s `Assert-PortFree` describes the same collision from the other side.

- [ ] **Step 6: Verify the Kubernetes gate refuses a missing cluster**

With no cluster deployed, run: `./deploy/e2e-k8s.ps1`
Expected: it throws, naming `./deploy/start-cluster.ps1`, and does **not** run Playwright.

- [ ] **Step 7: Verify the Kubernetes run end to end**

Run: `./deploy/start-cluster.ps1` then `./deploy/e2e-k8s.ps1`
Expected: the suite runs against `https://aiframework.localtest.me:8443` and passes.

This is the first run with `ForwardedHeaders__Enabled: 'true'` live, so it is also the
end-to-end proof of Task 1. If registrations start failing with 429, the flag is not reaching
the pods — check for a single underscore, and confirm with
`kubectl --context kind-aiframework -n aiframework get configmap app-config -o yaml`.

- [ ] **Step 8: Verify the control panel**

Double-click `local-run/control-panel.bat`, confirm options 1–9 render, and that 8 reports "No
e2e report found" gracefully if `playwright-report/` has been cleaned.

- [ ] **Step 9: Commit**

```bash
git add deploy/e2e-k8s.ps1 scripts/e2e.ps1 scripts/e2e-report.ps1 scripts/install-prereqs.ps1 local-run/control-panel.bat
git commit -m "$(cat <<'EOF'
feat(e2e): run the suite against kind, and reach both runs from the control panel

deploy/e2e-k8s.ps1 gates on GET /api/auth/me answering 401 rather than /health:
the ingress routes / to nginx, whose try_files falls back to index.html, so
/health answers 200 with the SPA even when the API is down.

install-prereqs.ps1 now checks Playwright's chromium binary, which npm ci does
not install - without it the new local option fails on a fresh machine with an
error naming neither the cause nor the fix.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01WaArfwR4LAikreBosVHZAM
EOF
)"
```

---

## Task 8: Conventions, documentation, and the ADR

Without this task the framework works and nobody knows the rules — which is the whole "more
people" requirement.

**Files:**
- Create: `frontend/e2e/CLAUDE.md`, `docs/adr/0012-end-to-end-test-architecture.md`
- Modify: `frontend/CLAUDE.md`, root `CLAUDE.md`, `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: everything.
- Produces: nothing.

- [ ] **Step 1: Write `frontend/e2e/CLAUDE.md`**

```markdown
# End-to-end tests

Playwright, against a real API and a real Postgres. See ADR 0012 for why it is shaped this way.

## The one import

A spec imports `test` and `expect` from `../../fixtures/index.ts` and nothing else from the
framework. Never `import { test } from '@playwright/test'` in a spec — that bypasses every
fixture below.

## Fixtures

| Fixture | Scope | Use it for |
|---|---|---|
| `signedInPage` | test | The default for anything needing a session |
| `page` | test | Anonymous visitors, and sign-in tests |
| `isolatedPage` / `freshUser` | test | See the rule below — not optional |
| `api` | test | Arranging data over HTTP |
| `workerUser` | worker | The user `signedInPage` is signed in as |

**`workerUser` registers once per worker, not once per test.** Auth calls therefore scale with
worker count, not test count. That is not an optimisation: `POST /api/auth/register` is rate
limited, the limiter partitions by client address, and against the kind cluster the whole suite
shares one partition with a budget of 10 per 60 seconds. A registration per test would cap a
cluster run at about ten tests a minute, surfacing as navigation timeouts that look like flakes.

## Two rules that are correctness, not style

1. **Anything that rotates the security stamp takes `isolatedPage`/`freshUser`.** That means
   changing a password, signing out everywhere, or tripping the account lockout. Rotating the
   stamp invalidates every cookie already issued for that user, so doing it to `workerUser`
   signs out every later test on that worker.
2. **Assert "contains", never "equals" or "is empty", on a list.** `workerUser` accumulates data
   within a run, and the kind cluster's Postgres is a StatefulSet with a PVC, so it accumulates
   across runs too. A test that genuinely needs an empty list takes `freshUser`.

## Screens

`screens/*.ts` own locators and interactions for one route. Plain exported functions, no classes.

- Locators are exported functions returning a `Locator`.
- Interactions are exported `async` functions that act and, where it is unambiguous, wait.
- **Assertions never live in a screen.** They belong in the spec, where the reader can see what
  the test claims.
- Queries stay role- and label-based. Do not add `data-testid`.
- Where two accessible names overlap — "Sign out" and "Sign out everywhere" — use
  `{ exact: true }`. Playwright matches names as substrings by default.

## Arranging data

Through the API, via the `api` fixture — never by driving another feature's UI, and never by
reaching into Postgres. Reaching into the database would not work against the cluster at all,
which is what the target switch exists for.

```ts
await api.placeOrder(workerUser, { sku, quantity: 3 });
await api.placeOrders(workerUser, 25);
```

Only the auth endpoints are rate limited, so bulk arrange is free on every target.

## Tags

| Tag | Meaning |
|---|---|
| `@local-only` | Needs something only the managed stack has |

Non-local runs use `--grep-invert @local-only`, so a test is assumed to run everywhere unless it
says otherwise. Add the tag when a test needs any of these:

- the cache off (`Cache__Enabled=false`) — the cluster runs with it on;
- the raised rate limit — the cluster allows 10 auth calls per 60 seconds;
- a database with nothing in it;
- a single API replica — the cluster runs two.

## Running it

| Command | Runs against |
|---|---|
| `npm run e2e` | A stack Playwright starts: compose Postgres, the API, the preview build |
| `npm run e2e:ui` | The same, in UI mode; keeps the database between runs |
| `npm run e2e:kind` | The deployed kind cluster |
| `npm run e2e:url -- https://…` | Any URL — including a dev loop already running on 5173 |
| `npm run e2e:report` | The last HTML report |

`./scripts/e2e.ps1`, `./deploy/e2e-k8s.ps1` and `./scripts/e2e-report.ps1` are the same things
from `local-run/control-panel.bat`.

The database prep runs from `setup/run.ts`, **before** Playwright starts — never as
`globalSetup`. Playwright launches `webServer` processes before `globalSetup`, so as a global
setup it arrived after the API had already tried and failed to boot against a database that did
not exist. Do not move it.
```

- [ ] **Step 2: Update `frontend/CLAUDE.md`**

In the commands table, replace the single `npm run e2e` row with the five scripts. Replace the
"Before the first `npm run e2e`" section's manual `npx playwright install chromium` instruction
with a note that `scripts/install-prereqs.ps1` now handles it, keeping the existing paragraph
about `prepare-database.ts` not becoming `globalSetup` and updating its path to
`e2e/setup/prepare-database.ts`. Add one line pointing at `frontend/e2e/CLAUDE.md` for the
conventions.

- [ ] **Step 3: Update the root `CLAUDE.md`**

- The `local-run/control-panel.bat` menu table: add options 6, 7, 8.
- The `install-prereqs.ps1` paragraph: add Playwright's chromium to the list it checks.
- The "Running on Kubernetes" section: add a line that `./deploy/e2e-k8s.ps1` runs the e2e suite
  against the cluster, and that it gates on `/api/auth/me` rather than `/health` because the
  ingress serves `/health` from nginx.
- Add `ForwardedHeaders__Enabled` to the config-keys warning about double underscores.

- [ ] **Step 4: Write `docs/adr/0012-end-to-end-test-architecture.md`**

Use the heading structure `docs/adr/0010-running-on-kubernetes.md` uses:

```markdown
# 0012. End-to-end test architecture

## Context
## Decision
## Consequences
### Accepted trade-offs
## Alternatives considered
```

It must record:

- Per-worker registration over HTTP, and the rate-limit arithmetic that forces it.
- Target switching, and that it rules out any database-level seeding.
- Fixtures plus screen modules, and why not page-object classes.
- No shared seed dataset; arrange through the API. Include the considered-and-rejected seeded
  cluster account.
- No cleanup of test users on the cluster, and that an admin delete endpoint was rejected as a
  production hole built for test convenience.
- The gated `UseForwardedHeaders` decision and the spoofing bypass the gate prevents. **This ADR
  amends ADR 0008**, so say so explicitly, the way ADR 0010 amends 0009.
- Kubernetes as an additional gate, and no CI job for it yet.

Note in passing that `docs/adr/0011` is cited by the root `CLAUDE.md` but was never written;
0012 leaves that number reserved rather than taking it.

- [ ] **Step 5: Add the report artifact note to CI**

`.github/workflows/ci.yml` needs no structural change — `npm run e2e --prefix frontend` still
works. Update the comment above the "Install Playwright browser" step, which currently says
`global-setup.ts` does the prep; the file is now `e2e/setup/prepare-database.ts`, invoked by
`e2e/setup/run.ts`.

- [ ] **Step 6: Verify everything**

Run, in order:

```bash
dotnet build -c Debug
dotnet test
npm run lint --prefix frontend
npm test --prefix frontend -- --run
npm run build --prefix frontend
npm run e2e --prefix frontend
```

Expected: all green, 12 e2e tests passing.

- [ ] **Step 7: Commit**

```bash
git add frontend/e2e/CLAUDE.md frontend/CLAUDE.md CLAUDE.md docs/adr/0012-end-to-end-test-architecture.md .github/workflows/ci.yml
git commit -m "$(cat <<'EOF'
docs: record the e2e conventions and ADR 0012

frontend/e2e/CLAUDE.md loads automatically in that directory and carries the two
rules that are correctness rather than style: session-invalidating tests take an
isolated user, and list assertions say "contains" because both the worker user
and the cluster's database accumulate data.

ADR 0012 amends ADR 0008 with the gated forwarded-headers decision.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01WaArfwR4LAikreBosVHZAM
EOF
)"
```

---

## Verification checklist

Run after Task 8. Every line must pass before the branch is considered done.

| Check | Command | Expected |
|---|---|---|
| Backend builds | `dotnet build -c Debug` | 0 warnings, 0 errors |
| Release builds | `dotnet build -c Release` | 0 warnings, 0 errors |
| Backend tests | `dotnet test` | All pass, including both `ForwardedHeadersTests` |
| Frontend lint | `npm run lint --prefix frontend` | Clean at `--max-warnings 0` |
| Frontend unit tests | `npm test --prefix frontend -- --run` | All pass, no Playwright spec picked up |
| Type-check | `npm run build --prefix frontend` | Clean |
| e2e, local | `npm run e2e --prefix frontend` | 12 pass |
| e2e, kind | `./deploy/e2e-k8s.ps1` | Passes after `./deploy/start-cluster.ps1` |
| e2e, refuses a missing cluster | `./deploy/e2e-k8s.ps1` with no cluster | Throws, names `start-cluster.ps1`, runs no tests |
| Control panel | double-click `local-run/control-panel.bat` | Options 1–9, 6/7/8 dispatch correctly |
