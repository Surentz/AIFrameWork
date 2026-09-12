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

**Two specs carry it today, and the arithmetic is worth spelling out.** `registration.spec.ts`
registers three times across its two tests (a UI register, an `api.register`, and the duplicate
attempt); `change-password.spec.ts` spends four across its two — the first test only registers, the
second registers and then signs in twice, once with the old password and once with the new.
Seven of the suite's twelve tests' worth of auth calls sit in those two files alone.
Run untagged against the cluster's shared 10-per-60-seconds partition, they would eat most of the
budget before the rest of the suite got a permit. **A `kind` run therefore executes 8 of the 12
tests, not all 12** — `npm run e2e` still runs all twelve locally, where the test host's limit is
raised out of the way (ADR 0008).

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

## Lint

`eslint.config.js` turns `react-hooks/rules-of-hooks` off for `e2e/**/*.ts`. The rule mistakes a
fixture's `use(...)` callback parameter for React 19's `use()` hook; this directory has no React
in it at all. Do not "fix" this by renaming the parameter or restoring the rule.
