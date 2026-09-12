# End-to-End Test Architecture — Design

**Date:** 2026-09-12
**Status:** Approved for planning
**Scope:** `frontend/e2e`, `frontend/playwright.config.ts`, `src/Api/Program.cs`,
`k8s/overlays/local/config.yaml`, `deploy/`, `scripts/`, `local-run/`,
`.github/workflows/ci.yml`

---

## 1. Context

The Playwright suite today is six tests in two files — `frontend/e2e/auth.spec.ts` (4) and
`frontend/e2e/orders.spec.ts` (2) — against a stack Playwright starts itself: `dotnet run` on
5234, `vite build && preview` on 4173, and a throwaway Postgres from `docker-compose.e2e.yml`
on 55432. `e2e/prepare-database.ts` brings the database up and migrates it *before*
`playwright test`, deliberately outside Playwright's `globalSetup`; `e2e/global-teardown.ts`
drops it with `docker compose down -v`.

It works, and every workaround in it is documented. What it lacks is structure: no fixtures,
no abstraction over locators, no tags or projects, no retry or trace policy, no way to point
at anything but the stack it builds, and `Date.now()`-based uniqueness that is safe only
because the suite is small.

The solution is small now and expected to grow along two axes: **many more features**, and
**more people writing tests**. This design is sized for that, not for today's six tests.

---

## 2. Goals

1. Make writing an e2e test cheap and uniform, so a new feature's coverage is a small,
   obvious diff rather than an act of invention.
2. Keep the suite correct at scale — no shared mutable state, no per-test cost that grows
   with test count.
3. Let the same specs run against the local compose stack **and** the kind Kubernetes
   cluster, where durable Wolverine, real caching, and two API replicas are in play.
4. Make the conventions discoverable without a person, via a directory `CLAUDE.md`.
5. Fix the rate-limiter proxy defect the investigation surfaced.

## 3. Non-goals

- Cross-browser or mobile coverage. Chromium only; the config is shaped so adding a browser
  is a three-line change.
- Sharding the suite in CI. The path is known (matrix + `blob` reporter + `merge-reports`)
  and costs more than it saves at this size.
- A Kubernetes e2e job in CI.
- Visual regression or accessibility scanning.
- Any admin or test-only endpoint in the API.

---

## 4. Findings that drove the design

Established by reading the code, not assumed.

### F1 — Registration is open everywhere, but the rate limiter collapses behind a proxy

`POST /api/auth/register` is `[AllowAnonymous]` in every environment
(`src/Api/Auth/AuthController.cs:21`), so a test can always create its own user over HTTP.

But the auth policy partitions on `httpContext.Connection.RemoteIpAddress`
(`src/Api/Program.cs:142`) and **no `UseForwardedHeaders` is registered anywhere in
`Program.cs`**. Behind ingress-nginx every request appears to originate from the ingress
controller pod, so the entire suite shares one partition. The kind overlay sets
`RateLimiting__Auth__PermitLimit: '10'` / `WindowSeconds: '60'`
(`k8s/overlays/local/config.yaml`).

**Consequence:** one registration per test caps a remote run at roughly ten tests per minute,
surfacing as `waitForURL` timeouts indistinguishable from flakes. Registration must become
**per worker**, not per test. With two workers a 200-test run makes two auth calls, so the
budget holds at any suite size.

### F2 — `/health` through the ingress is a false positive

The ingress routes `/api` to the API and `/` to web (`k8s/base/ingress.yaml`), and
`frontend/nginx.conf` ends in `try_files $uri $uri/ /index.html`. A request for
`https://aiframework.localtest.me:8443/health` therefore reaches **nginx**, matches no file,
and returns **200 with `index.html`** — reporting the stack healthy while the API is down.

**Consequence:** the readiness gate for a cluster run must use a real API path.
`GET /api/auth/me` answering **401** proves the API is alive and that ingress routing works.

### F3 — The ingress HTTPS port is derived, not fixed

`deploy/deploy.ps1` regex-parses `deploy/kind-cluster.yaml` for the `containerPort: 443` →
`hostPort` mapping (8443 today, because host 443 is held by `http.sys` on this machine) and
prints the URL from it.

**Consequence:** the e2e target must resolve the port the same way, or the two drift silently
the moment port 443 frees up.

### F4 — The cluster database persists across redeploys

Postgres in the cluster is a StatefulSet with a 2Gi `volumeClaimTemplate`
(`k8s/base/postgres.yaml`). `deploy.ps1` redeploys onto it; only `deploy/teardown.ps1`
(`kind delete cluster`) destroys the data. Unlike the compose stack, which is `down -v` every
run, **e2e users and orders accumulate on the cluster**.

**Consequence:** no test may assume a clean database. "Assert contains, never equals" becomes
a correctness rule, not a style preference.

### F5 — The cluster differs from the e2e stack in exactly the interesting ways

| | local e2e | kind |
|---|---|---|
| `Wolverine__Durable` | `false` | `true` |
| `Cache__Enabled` | `false` | `true` |
| `RateLimiting__Auth__PermitLimit` | `1000000` | `10` |
| API replicas | 1 | 2, behind cookie affinity |

**Consequence:** a kind run is not a slower copy of the local run. It is the only thing that
exercises the durable outbox, real cache eviction, and ADR 0010's cookie-affinity claim —
which is currently an argument in a document with no test behind it.

### F6 — `e2e/` is already type-checked and linted

`frontend/tsconfig.node.json` includes `e2e` with the full repo strictness delta, and
`frontend/eslint.config.js` matches `**/*.{ts,tsx}` with `projectService`.

**Consequence:** promoting the suite to a top-level workspace would buy organisational
tidiness and cost a second `package.json`, a second lockfile, and a second `npm ci` in CI
(there is no root `package.json` to hang a workspace off). It stays in `frontend/e2e/`.

### F7 — Usernames are constrained

`User.MaxUsernameLength` is 32 and `RegisterUserValidator` allows only
`[A-Za-z0-9._-]`. `PasswordPolicy.MinimumLength` is 12.

**Consequence:** `e2e-` plus 12 hex characters from `crypto.randomUUID()` is valid and
collision-free, replacing `Date.now()`, which collides under parallel workers.

### F8 — `docs/adr/0011` is referenced but does not exist

`CLAUDE.md:291` and `CLAUDE.md:299` cite ADR 0011 for session invalidation; commit `3c31998`
updated `CLAUDE.md` without adding the file. Out of scope here; this design takes **0012** and
leaves 0011 reserved.

---

## 5. Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | Stay in `frontend/e2e/`, restructure internally | F6 |
| D2 | Fixtures + screen modules; no page-object classes | Single source of truth for locators without class ceremony; composes with Playwright's grain |
| D3 | Register **once per worker** over HTTP, reuse via `storageState` | F1 — the only thing that makes a remote run possible at scale |
| D4 | A separate `freshUser` / `isolatedPage` for session-invalidating tests | Password change and sign-out-everywhere rotate the security stamp and would break the worker's shared cookie |
| D5 | Assert "contains", never "equals" or "is empty", on any list | F4 |
| D6 | Target resolution in one module; `webServer` conditional on it | Lets the same specs run local, kind, or any URL |
| D7 | Opt-**out** tagging (`@local-only`), not opt-in (`@smoke`) | An opt-in set decays; the opt-out failure mode is loud and immediate |
| D8 | No shared seed dataset; arrange per test through the app's API | Works on every target, no shared mutable rows, no second source of truth |
| D9 | Kubernetes is an additional gate, not the primary run | F5 makes it valuable; build-and-deploy cost makes it unsuitable as the everyday loop |
| D10 | `UseForwardedHeaders` added, gated by config, default off | F1 — and clearing proxy trust unconditionally would turn `X-Forwarded-For` into a rate-limiter bypass |
| D11 | One Node entry point (`run.ts`) replaces the `&&` script chain | Makes DB prep unforgettable and works identically in PowerShell and bash |
| D12 | Both e2e runs, and the report, reachable from `control-panel.bat` | It is the front door for people who do not open a terminal; an e2e suite they cannot start is one they will not run |
| D13 | `install-prereqs.ps1` gains a Playwright chromium check | Without it D12's local option fails on a new machine with an error naming neither cause nor fix |

---

## 6. Layout

`frontend/playwright.config.ts` stays where it is. Under `frontend/e2e/`:

```
fixtures/
  index.ts       the extended `test` + `expect` — the ONE import a spec makes
  users.ts       workerUser (worker-scoped), freshUser (test-scoped)
  api.ts         ApiClient over APIRequestContext, typed from src/api/schema.d.ts
screens/
  shell.ts       AppLayout: nav, sign-out button
  login.ts  register.ts  orders.ts  account.ts
support/
  env.ts         ports (existing, extended)
  target.ts      which environment, and whether we manage its stack
  identity.ts    uniqueUsername(), PASSWORD
setup/
  run.ts                entry point: sets the target, preps if needed, spawns playwright
  prepare-database.ts   (moved, behaviour unchanged)
  global-teardown.ts    (moved, made target-aware)
specs/
  auth/   orders/
```

A spec's only framework import is `from '../../fixtures'`. That single rule is what stops five
people inventing five ways in.

---

## 7. Fixtures

```ts
// worker-scoped
workerUser:   TestUser           // { username, password, userId }
workerState:  StorageStatePath   // cookie jar captured from the registration call

// test-scoped
signedInPage: Page       // context carrying workerState — the default
freshUser:    TestUser   // a brand-new user, registered via the API, for this test only
isolatedPage: Page       // context signed in as freshUser
api:          ApiClient  // typed request client; can act as any TestUser
```

`workerUser` registers over **HTTP, not the UI** — `request.post('/api/auth/register')`, then
`APIRequestContext.storageState()` to capture the cookie. A 40-test run on four workers makes
four auth calls instead of forty (F1), and saves roughly a second per test locally.

Registration keeps **one** UI test of its own, because nothing else exercises that form any
more. That is the deliberate trade: the flow is tested once on purpose rather than forty times
by accident.

### Two rules that follow, and are not optional

1. **Any test that changes a password, signs out everywhere, or triggers lockout MUST take
   `isolatedPage`/`freshUser`, never `signedInPage`.** Those operations rotate
   `User.SecurityStamp`, which invalidates the worker's shared cookie and would break every
   later test on that worker. This is the session-invalidation rule from `CLAUDE.md` showing
   up in the test design.
2. **Assert "contains", never "equals" or "is empty", on any list.** A worker user accumulates
   data within a run, and the cluster accumulates it across runs (F4). A test that genuinely
   needs an empty list takes `freshUser`.

---

## 8. Screens

Plain modules. No classes, no inheritance. Locators are exported functions; interactions are
exported async functions; **assertions never live here** — they stay in the spec.

```ts
// screens/orders.ts
export const skuField      = (p: Page) => p.getByLabel('Sku');
export const quantityField = (p: Page) => p.getByLabel('Quantity');
export const submitButton  = (p: Page) => p.getByRole('button', { name: 'Place order' });
export const orderLink     = (p: Page, sku: string) => p.getByRole('link', { name: sku });

export async function placeOrder(p: Page, o: { sku: string; quantity: number }): Promise<void> {
  await p.goto('/orders/new');
  await skuField(p).fill(o.sku);
  await quantityField(p).fill(String(o.quantity));
  await submitButton(p).click();
}
```

Queries stay role- and label-based, as the existing specs already do. No `data-testid` retrofit.

---

## 9. Targets

```ts
type Target = {
  name: string;
  baseURL: string;
  managesStack: boolean;      // start webServer, migrate, tear down?
  ignoreHTTPSErrors: boolean;
};
```

| `E2E_TARGET` | baseURL | managesStack |
|---|---|---|
| unset / `local` | `http://localhost:${PREVIEW_PORT}` | yes |
| `kind` | `https://aiframework.localtest.me:<port from kind-cluster.yaml>` (F3) | no |
| any URL | that URL | no |

`webServer` becomes `target.managesStack ? [...existing two entries] : undefined`.
`ignoreHTTPSErrors` is on only for `kind`, whose certificate is self-signed — not blanket-on,
which would hide real TLS problems.

### Entry point

`e2e/setup/run.ts` (~25 lines) sets `E2E_TARGET`, runs DB prep when the target manages its own
stack, then spawns `playwright test` forwarding any extra arguments. **DB prep stays outside
Playwright** — `prepare-database.ts`'s existing comment explains why it must never become
`globalSetup`, and that constraint is unchanged; the runner only makes it unforgettable. It
also sidesteps `E2E_TARGET=kind npm run e2e` not working in PowerShell, without adding
`cross-env`.

```jsonc
"e2e":        "node e2e/setup/run.ts",
"e2e:ui":     "node e2e/setup/run.ts --ui",        // implies keep-database
"e2e:kind":   "node e2e/setup/run.ts --target kind",
"e2e:url":    "node e2e/setup/run.ts --target",    // npm run e2e:url -- https://…
"e2e:report": "playwright show-report"
```

`reuseExistingServer: false` is deliberately **unchanged**. The target mechanism supersedes it:
if a dev loop is already running, `npm run e2e:url http://localhost:5173` points at it, with
no risk of silently reusing a stale server, and `@local-only` tests correctly skip because the
dev API has caching on.

---

## 10. Tagging, parallelism, reliability

| Tag | Meaning |
|---|---|
| `@local-only` | Needs a knob only the managed stack has (`Cache__Enabled=false`, the raised rate limit) or a pristine database |
| `@smoke` | A named subset, for a 60-second answer |

Non-local runs use `--grep-invert @local-only` (opt-out). Opt-in via `@smoke` looks safer but
decays: a set nobody remembers to extend is worthless within months. The opt-out failure mode —
a new test that needs a clean database fails against kind — is loud, immediate, and fixed by
adding one tag. The conditions forcing `@local-only` go in the conventions doc as a checklist.

```ts
fullyParallel: true,
forbidOnly: !!process.env.CI,
retries: process.env.CI ? 1 : 0,
workers: target.managesStack ? undefined : 2,
timeout: target.managesStack ? 30_000 : 60_000,
expect: { timeout: 10_000 },
use: { trace: 'on-first-retry', screenshot: 'only-on-failure', video: 'retain-on-failure' },
reporter: [['list'], ['html', { open: 'never' }], ...(CI ? [['github']] : [])],
projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
```

Auth calls scale with **worker count, not test count** (F1), so two workers remotely keeps any
suite size inside the ten-per-sixty-seconds budget. `expect.timeout` rises from Playwright's
5s default because a cold .NET first request through an ingress is genuinely slower than that.

### Teardown

`global-teardown.ts` becomes a no-op when `managesStack` is false. It also gains
`E2E_KEEP_DATABASE=1`, which `--ui` sets automatically: today the teardown drops the database
**even when the run failed**, destroying the evidence, and UI-mode iteration wants the
container to survive between runs.

---

## 11. Data setup — no seed dataset, arrange through the API

**Rejected: a shared seed dataset (SQL or fixture file loaded into the database).**

- It cannot work uniformly across targets. There is no database access against kind through
  the ingress, which would break D6 outright.
- Shared rows plus parallel workers is the classic e2e flake source.
- It becomes a second source of truth that drifts from the domain model.
- There are two entities. An order is one HTTP call. It buys little and costs maintenance
  forever.

**Adopted: per-test arrange through the application's own API.** The `api` fixture carries
task helpers:

```ts
await api.placeOrder(workerUser, { sku, quantity: 3 });
await api.placeOrders(workerUser, 25);      // e.g. a paginated-list test
```

`frontend/src/api/orders.ts` already exposes `cursor` and `limit`, so paginated-list tests are
a near-term case: 25 orders is 25 parallel HTTP calls in under a second, against roughly forty
seconds of form-filling. It behaves identically on every target. And only the auth endpoints
carry `[EnableRateLimiting]` — `POST /api/orders` does not — so bulk arrange is free even
against the cluster's budget.

**Considered and rejected:** seeding a known e2e account into the cluster at deploy time, which
would drop remote auth calls to zero. Unnecessary — two workers means two registrations against
a budget of ten. Recorded in the ADR as a deliberate non-decision.

**Cleanup on kind: none, deliberately.** Removing test users would require an admin delete
endpoint, and building a destructive endpoint for test convenience is how a production hole
gets created. The documented recycle is `deploy/teardown.ps1` then `deploy/start-cluster.ps1`.

---

## 12. The Kubernetes target

Role: an **additional gate**, not the everyday loop (D9). The local compose run stays fast,
isolated, and clean; the kind run covers what only it can (F5).

`deploy/e2e-k8s.ps1`:

1. Resolves the HTTPS host port from `deploy/kind-cluster.yaml` with the same regex
   `deploy.ps1` uses (F3).
2. Verifies the stack answers, using `GET /api/auth/me` → **401** as the gate, never `/health`
   (F2).
3. **Assumes the cluster is already deployed.** If it does not answer, it aborts with a message
   naming `deploy/start-cluster.ps1` rather than silently triggering three docker builds and a
   rollout.
4. Runs Playwright with `E2E_TARGET=kind` and `--grep-invert @local-only`.

---

## 12a. The control panel

`local-run/control-panel.bat` is the front door for anyone who would rather not open a
terminal, and its header states it has no logic of its own beyond the menu. Both e2e runs are
reachable from it, each as one entry calling one `.ps1`, keeping that property intact.

| Option | Runs | Script |
|---|---|---|
| 1–5 | unchanged | — |
| **6. Run e2e tests (local stack)** | compose Postgres, API, preview build, full suite | `scripts/e2e.ps1` (new) |
| **7. Run e2e tests (Kubernetes)** | the deployed cluster, `--grep-invert @local-only` | `deploy/e2e-k8s.ps1` (new) |
| **8. Open last e2e report** | `playwright show-report` | `scripts/e2e-report.ps1` (new) |
| 9. Exit | unchanged | — |

`scripts/e2e.ps1` is a thin wrapper over `npm run e2e --prefix frontend`, so the npm scripts
stay the source of truth exactly as `scripts/dev.ps1` does for the dev loop.

### Prerequisite, currently missing

`npm run e2e` needs the Playwright browser binary — `npx --prefix frontend playwright install
chromium` — a one-off per machine documented in `frontend/CLAUDE.md` but **not** checked by
`scripts/install-prereqs.ps1`, which covers the .NET SDK, Node, Docker Desktop, `kubectl`,
`kind`, and `k9s`.

Without it, option 6 fails on a new machine with a Playwright error that names neither the
cause nor the fix — precisely the experience the control panel exists to prevent. So
`install-prereqs.ps1` gains a chromium check, installing it when absent. It follows that
script's existing rule: install only what is entirely missing, report anything present but
outdated rather than silently upgrading it.

Option 8 is listed separately rather than folded into the run scripts because the report is
worth reopening after the terminal window has been closed, which is the normal case for a
double-click user.

---

## 13. Backend: `UseForwardedHeaders`, gated

The naive fix opens a larger hole than it closes. ASP.NET trusts only loopback proxies by
default; behind ingress-nginx the proxy is a cluster-assigned pod IP, so making it work at all
requires clearing `KnownNetworks`/`KnownProxies`. Once trust is cleared, anyone who can reach
the API directly can spoof `X-Forwarded-For` and mint a fresh rate-limit partition per
request — a complete bypass of ADR 0008's volume defence.

Therefore it is configuration-gated and **off by default**, registered before
`app.UseRateLimiter()` (currently `src/Api/Program.cs:224`):

```csharp
if (builder.Configuration.GetValue("ForwardedHeaders:Enabled", defaultValue: false))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = 1;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });
}
```

- **`XForwardedFor` only.** Nothing reads `Request.IsHttps` — `CookieSecurePolicy.Always` is
  unconditional in Production — so `XForwardedProto` would change behaviour for no benefit.
  Excluded on purpose.
- **Ordering is load-bearing.** After `UseRateLimiter()` the limiter reads the pre-rewrite
  address and nothing changes.
- `k8s/overlays/local/config.yaml` gains `ForwardedHeaders__Enabled: 'true'` — double
  underscore. `dotnet run`, the e2e stack, and the integration tests are unaffected.

**Tests** (`tests/Api.IntegrationTests`): with the flag **off**, a spoofed `X-Forwarded-For`
must be ignored; with it **on**, two different values must land in different partitions. The
off-case is the regression guard that stops the flag later being "simplified" away.

**Settled during implementation, not merely assumed:** `WebApplicationFactory`'s TestServer
leaves `Connection.RemoteIpAddress` null — which is why `Program.cs`'s rate-limiter partition
key has its `"unknown"` fallback. `ForwardedHeadersMiddleware` rewrites cleanly from that null
starting point: both `ForwardedHeadersTests` below pass with no `IStartupFilter` or other
fallback to give TestServer a non-null starting address. Also note for any future reader of the
code block above: this SDK ships `ForwardedHeadersOptions.KnownIPNetworks`, not `KnownNetworks`
— the old name is obsolete (ASPDEPR005, an error here since warnings are errors) — and a
reviewer confirmed by reflection that the two properties are the same object instance, so
clearing one clears both. The rest of this section is left as drafted, as the historical record
of the question this answers.

**This fix does not raise the suite's own ceiling.** Every worker on one machine shares a
client IP and so would still share a partition. It is a production correctness fix — without
it, one abusive client behind any proxy rate-limits everybody — and D3 is what makes remote
runs work regardless.

---

## 14. CI

`.github/workflows/ci.yml`'s `e2e` job keeps its shape.

- `npm run e2e --prefix frontend` still works; the runner sits behind the same script name.
- The `github` reporter adds inline PR annotations on failure. `playwright-report/` already
  uploads on failure and carries traces, so `trace: 'on-first-retry'` needs no new step.
- **No sharding.** At this size, runner startup costs more than it saves.
- **No Kubernetes job.** Cluster creation, three image builds, and ingress setup would add ten
  or more minutes per run. Recorded in the ADR; revisit if the local k8s path shows rot.

---

## 15. Test inventory

Ported unchanged in behaviour, rewritten onto fixtures and screens:

| Spec | Tests |
|---|---|
| `specs/auth/sign-in.spec.ts` | sign out and back in · anonymous visitor redirected · session survives reload · wrong password message |
| `specs/orders/place-order.spec.ts` | places an order and sees it in the list |
| `specs/orders/validation.spec.ts` | server validation message for an invalid quantity |

New:

| Spec | Tests | Fixture |
|---|---|---|
| `specs/auth/registration.spec.ts` | registers through the UI (the one UI registration test) · duplicate username shows the 409 message | `api` |
| `specs/auth/change-password.spec.ts` | old password rejected, new accepted · **caller stays signed in afterwards** | `isolatedPage` |
| `specs/auth/sign-out-everywhere.spec.ts` | two browser contexts, same user; signing out everywhere in one bounces the other to `/login` | `isolatedPage` |
| `specs/orders/order-detail.spec.ts` | list → detail navigation renders the order | `signedInPage` |

Two of these earn their keep specifically as end-to-end tests:

- **"caller stays signed in after changing their own password"** is the exact regression
  `CLAUDE.md` warns about: without the cookie re-issue in `AuthController`, changing your
  password signs you out. Nothing below e2e can catch it, because it is about a real browser
  holding a real cookie.
- **sign-out-everywhere across two contexts** is untestable at any lower layer; it needs two
  independent cookie jars.

And one deliberate non-tag: **"places an order and sees it in the list" stays untagged**, so it
runs against kind with the cache **on**. That makes it an end-to-end check of ADR 0010's claim
that ingress cookie affinity keeps L1 eviction correct across two pods.

---

## 16. Documentation deliverables

- **`frontend/e2e/CLAUDE.md`** (new) — the real deliverable for "more people". Loads
  automatically when anyone works in that directory. Covers: the one-import rule;
  `signedInPage` vs `isolatedPage` and why; assert-contains-never-equals; the `@local-only`
  checklist; screens never assert; how to run against each target; the per-worker auth budget.
- **`frontend/CLAUDE.md`** — e2e section updated for the new paths and scripts, keeping its
  existing warning about `prepare-database.ts` not becoming `globalSetup`.
- **Root `CLAUDE.md`** — the e2e lines under "Running locally", "Running on Kubernetes", and
  "CI"; the control-panel menu table gains the three new options (§12a), and the
  `install-prereqs.ps1` paragraph gains the Playwright browser.
- **`docs/adr/0012-end-to-end-test-architecture.md`** — seeding strategy, target switching,
  the abstraction layer, and the forwarded-headers trust decision. It **amends ADR 0008**, the
  way ADR 0010 amends 0009.

---

## 17. Risks and open items

| Risk | Mitigation |
|---|---|
| `ForwardedHeadersMiddleware` behaviour under TestServer's null `RemoteIpAddress` | **Settled.** It rewrites cleanly from null; both `ForwardedHeadersTests` pass with no fallback needed. See §13. |
| Someone enables `ForwardedHeaders__Enabled` where the API is directly reachable | Default off; the justification comment and the ADR both name the bypass; the flag-off integration test guards the default |
| A new test assumes a clean database and fails only against kind | `@local-only` checklist in `frontend/e2e/CLAUDE.md`; failure is immediate and names the cause |
| The kind path rots because it is not in CI | Accepted (D9, §14); revisit with a scheduled job if it happens |
| `docs/adr/0011` remains a dangling reference | Out of scope; recorded as F8 so it is not lost |
