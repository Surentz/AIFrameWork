# End-to-end tests

Playwright, against a real API, a real job worker, a real Postgres and a real RabbitMQ. See ADR
0012 for why it is shaped this way, ADR 0023 for why the worker is part of it, and ADR 0026 for
the broker.

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
| `adminPage` / `adminUser` | test / worker | The operator's screens — monitoring, fulfilment, the catalogue forms. Loaded from a session signed in once per run; see rule 3 |
| `api` | test | Arranging data over HTTP |
| `openSession` | test | A further signed-in page for a given user — e.g. a second browser. Closed at teardown |
| `workerUser` | worker | The user `signedInPage` is signed in as |

**`workerUser` registers once per worker, not once per test.** Auth calls therefore scale with
worker count, not test count. That is not an optimisation: `POST /api/auth/register` is rate
limited, the limiter partitions by client address, and against the kind cluster the whole suite
shares one partition with a budget of 10 per 60 seconds. A registration per test would cap a
cluster run at about ten tests a minute, surfacing as navigation timeouts that look like flakes.

## Three rules that are correctness, not style

1. **Anything that rotates the security stamp takes `isolatedPage`/`freshUser`.** That means
   changing a password, signing out everywhere, or tripping the account lockout. Rotating the
   stamp invalidates every cookie already issued for that user, so doing it to `workerUser`
   signs out every later test on that worker.
2. **Assert "contains", never "equals" or "is empty", on a list.** `workerUser` accumulates data
   within a run, and the kind cluster's Postgres is a StatefulSet with a PVC, so it accumulates
   across runs too. A test that genuinely needs an empty list takes `freshUser`.
3. **`adminUser` is the one fixed username in the suite, and it must stay fixed.** The
   administrator role is granted solely by the API's `Admin__Usernames` (ADR 0020):
   `playwright.config.ts` names it on the stack it starts, and `k8s/overlays/local/config.yaml`
   on the cluster — a generated name could never appear in a config written before the run.
   **Nothing but `e2e/setup/seed-admin.ts` signs in as it.** That `globalSetup` runs once per run
   on every target, signs in (registering only on a fresh database), and saves the session to the
   git-ignored `e2e/.auth/admin.json`; the fixture loads that file. Workers never sign in as the
   operator, so it costs the auth budget one call however many workers there are. Writing to the
   catalogue needs it (ADR 0025), and the `api` fixture routes every catalogue write through it —
   so an arbitrary URL target has to name `e2e-admin` too, or nearly every spec fails with a 403.

## Screens

`screens/*.ts` own locators and interactions for one route. Plain exported functions, no classes.

- Locators are exported functions returning a `Locator`.
- Interactions are exported `async` functions that act and, where it is unambiguous, wait.
- **Assertions never live in a screen.** They belong in the spec, where the reader can see what
  the test claims.
- Queries stay role- and label-based. Do not add `data-testid`.
- Row- or dialog-scoped buttons get a screen function too (`monitoring.userAction`), not an
  inline `getByRole` in the spec.
- Where two accessible names overlap — "Sign out" and "Sign out everywhere" — use
  `{ exact: true }`. Playwright matches names as substrings by default.

## Specs

- A test with more than one phase wraps each in `test.step('…')`, so the report and trace say
  which phase failed rather than showing one unbroken block. A three-line test needs none.
- Never close a context at the end of a test body — a failed assertion skips that line and leaks
  it. Open extra pages through `openSession`, which closes them at teardown either way.

## Arranging data

Through the API, via the `api` fixture — never by driving another feature's UI, and never by
reaching into Postgres. Reaching into the database would not work against the cluster at all,
which is what the target switch exists for.

```ts
await api.placeOrder(workerUser, { sku, quantity: 3 });   // the operator creates the product
await api.placeOrders(workerUser, 25);
const id = await api.createProduct({ sku, name: sku, price: '19.95' });  // always the operator
await api.orderProduct(workerUser, { sku, quantity: 1 });  // a product that already exists
await api.shipOrder(adminUser, orderId);                  // the operator ships: @local-only
await api.cancelOrder(workerUser, orderId, 'reason');
await api.updateProduct(id, { name: sku, price: '9.95' });                // always the operator
await api.failSignIn(freshUser.username);                  // never workerUser: see rule 1
```

Only `register` and `login` are rate limited, so bulk arrange is free on every target —
`failSignIn` is the one helper that spends a permit.

## Waiting for background work

Two things happen after the request that caused them returns: notifications (written by the
outbox pump) and job runs (executed by the worker). **Wait for them over HTTP, then navigate.**
Never reload a page in a loop until a row appears.

```ts
await api.waitForNotification(workerUser, { kind: 'OrderPlaced', text: sku });
await expect.poll(() => api.countJobRuns(adminUser, Job, 'Succeeded')).toBeGreaterThan(before);
```

`countJobRuns` is compared against a count taken *before* acting: scheduled jobs have a cron and
earlier runs, so "a row exists" proves nothing. Locate a notification by its **body**, which
carries the unique sku; its title ("Order placed") is shared by every order.

## Tags

| Tag | Meaning |
|---|---|
| `@local-only` | Needs something only the managed stack has |

Non-local runs use `--grep-invert @local-only`, so a test is assumed to run everywhere unless it
says otherwise. Add the tag when a test needs any of these:

- the cache off (`Cache__Enabled=false`) — the cluster runs with it on;
- the raised rate limit — the cluster allows 10 auth calls per 60 seconds;
- a database with nothing in it;
- a single API replica — the cluster runs two;
- the host-exposed broker — the kind cluster exposes no RabbitMQ port to the host.

It is carried for three reasons, and the arithmetic behind the first is worth spelling out.

**The auth budget.** Only `register` and `login` spend permits, and the cluster allows 10 per 60
seconds for the whole suite. A run spends **eight**: the operator's one sign-in in
`seed-admin.ts` (two on a cluster that has never seen it — a refused sign-in, then the
registration), two `workerUser` registrations (one per worker), three `freshUser` registrations
(`sign-out-everywhere`, the bell test in `feed.spec.ts`, `order-list.spec.ts`), and two sign-ins
(`sign-in.spec.ts`'s sign-out-and-back-in and wrong-password tests). That leaves one permit of
headroom, none on a cluster's first run. Anything that would push it further is tagged instead:
`registration.spec.ts`, `change-password.spec.ts`, `lockout.spec.ts` (six permits on its own),
and the remember-me test. **Recount before adding an untagged test that registers or signs in.**

**The administrator, and the worker.** `monitoring.spec.ts`, `users.spec.ts`, `jobs.spec.ts` and
`logins.spec.ts` were tagged because only the managed stack named the e2e operator. The kind
overlay names it too since ADR 0025, so that reason no longer holds for the cluster; they stay
tagged until someone decides to run them there, which is its own change.
`jobs.spec.ts` also needs the worker, which an arbitrary URL target cannot be assumed to run, and
`exports.spec.ts` is tagged for that reason alone.
`monitoring.spec.ts`'s two *access* tests are untagged deliberately — refusing a member is the
security-relevant half and needs no administrator, so it runs everywhere.

**Shipping, likewise.** It is the operator's since ADR 0024, so `api.shipOrder` takes
`adminUser`: `fulfilment.spec.ts`'s queue tests and `feed.spec.ts`'s shipped-notification test are
tagged. The feed's cancellation test was split out of the shipped one to stay untagged — the buyer
cancels for themselves — and `fulfilment.spec.ts`'s member refusal is untagged for the reason the
monitoring access tests are. Neither spends an auth permit: both run as `workerUser`.

**The catalogue is not tagged.** Its forms are the operator's (ADR 0025), and `catalogue.spec.ts`
drives them as `adminPage` — on the cluster too, because the kind overlay names the operator. Its
member refusal and paging tests run as `workerUser`.

**The broker, for a third reason.** `shipment-inbound.spec.ts` is tagged for neither the auth
budget nor the administrator: it publishes `shipment.confirmed.v1` through the broker's
management HTTP API directly from the host (`support/broker.ts`), and only the stack
`playwright.config.ts` starts exposes that port — the kind cluster does not.

**A `kind` run therefore executes 28 of the 59 tests** — `npm run e2e` runs all of them, where
the test host's limit is raised out of the way (ADR 0008).

## Running it

| Command | Runs against |
|---|---|
| `npm run e2e` | A stack Playwright starts: compose Postgres and RabbitMQ, the API, the worker, the preview build |
| `npm run e2e:ui` | The same, in UI mode; keeps the database between runs |
| `npm run e2e:kind` | The deployed kind cluster |
| `npm run e2e:url -- https://…` | Any URL — including a dev loop already running on 5173 |
| `npm run e2e:report` | The last HTML report |
| GitHub → Actions → **e2e** → *Run workflow* | The managed stack on a runner, for any branch; optional `grep` and `repeat_each` |

`./scripts/e2e.ps1`, `./deploy/e2e-k8s.ps1` and `./scripts/e2e-report.ps1` are the same things
from `local-run/control-panel.bat`.

The database prep runs from `setup/run.ts`, **before** Playwright starts — never as
`globalSetup`. Playwright launches `webServer` processes before `globalSetup`, so as a global
setup it arrived after the API had already tried and failed to boot against a database that did
not exist. Do not move it.

Teardown is the mirror image: `setup/teardown-database.ts`, run by `run.ts` **after** Playwright
exits. Not `globalTeardown`, which runs before the `webServer`s stop and so took Postgres away
from a live API and worker.

The API and worker log at Warning; `E2E_SERVER_LOG_LEVEL=Information` brings their full output
back when a failure needs it.

The same step **builds both .NET hosts**, and the `webServer`s start them with `--no-build`:
Playwright launches them in parallel, and two `dotnet run` builds of the projects they share race
on the same `obj/` files. A consequence: `npx playwright test` run directly, bypassing `run.ts`,
starts whatever was last built.

The managed stack uses ports 5234 (API), 5235 (worker), 4173 (preview), 55432 (Postgres),
55682/55683 (RabbitMQ, AMQP and management UI) and 55692/55693 (the partner simulator: mTLS and health; `SIMULATOR_PORT` / `SIMULATOR_HEALTH_PORT`) — the first two are also the dev loop's. Stop it
first, or set `API_PORT` / `WORKER_PORT`.

The simulator is a fourth `webServer` (`tests/PartnerSimulator -- serve`). Its certificates live in
`frontend/e2e/.certs/` (git-ignored) and are **reused across local runs**, regenerated only when a
file is missing or `client.pfx` is over 365 days old: each fresh PKI leaks one same-subject
intermediate into Windows `CurrentUser\CA`. CI generates them every run. `integrations.spec.ts` is
`@local-only` and `test.slow()` (the worker checks every 5 s in e2e, so it waits on the page's 30 s refetch).

## What is covered

| Area | Specs |
|---|---|
| Auth | sign-in, remember me, reveal password, navigation, registration (+ validation), change password (+ validation), sign out everywhere, lockout |
| Orders | place, list + paging + empty state, detail (price, total, product link), validation, fulfilment (member refused, operator ships from the queue, cancelled confirmation), shipment confirmed via the broker, export to PDF (request, built by the worker, notified, read in the in-app viewer — drawn by pdf.js, Esc closes, focus returns — and downloaded) |
| Products | create, edit, edit-from-detail, duplicate sku, field validation (all as the operator), member refused the form, paging |
| Notifications | placed, shipped (by the operator), cancelled, price changed, View links, mark read, unread filter, bell count, mark all read |
| Monitoring | access, overview, drill-downs, traffic window, external systems (simulator healthy, dead address unhealthy), jobs (trigger, order confirmation on the worker), sign-ins (audit filter, locked accounts), users (promote, demote, cancel, sign out, history, search) |

Not covered end to end, deliberately: the dead-letter retry (nothing dead-letters on purpose),
traffic numbers (a clock race against the minute flush — ADR 0021), and realtime push (off in the
managed stack; the feed is the truth — ADR 0019).

## Lint

`eslint.config.js` turns `react-hooks/rules-of-hooks` off for `e2e/**/*.ts`. The rule mistakes a
fixture's `use(...)` callback parameter for React 19's `use()` hook; this directory has no React
in it at all. Do not "fix" this by renaming the parameter or restoring the rule.
