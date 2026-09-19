# Frontend

Vite + React workspace, TypeScript, client-side SPA.

## After `npm create vite`, apply this `tsconfig.json` delta

The generator writes this file, so these cannot be pre-written — apply them once:

```jsonc
{
  "compilerOptions": {
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "exactOptionalPropertyTypes": true,
    "noImplicitOverride": true,
    "noFallthroughCasesInSwitch": true,
    "noImplicitReturns": true
  }
}
```

## Conventions

- **Function components only.** No classes.
- **Explicit prop interfaces**, explicit return types on exported functions.
- **TanStack Query owns server state.** No `useEffect` data fetching.
- **Query keys live in one object per feature.** Never inline a key literal.
- **A list-changing mutation invalidates that list** in `onSuccess`.
- **Every query and mutation renders its error state.** A fetch with no error branch is
  incomplete.
- **Never swallow an error.** No `catch {}`, no `.catch(() => null)` to silence a red line.
- **No `any`, no `!`.**

## The API proxy

Dev runs two processes: Vite on 5173 and the API on 5234. `vite.config.ts` proxies `/api` to
`http://localhost:5234`, so the browser sees one origin and the backend needs no CORS
configuration. Production is a static bundle.

**`/hubs` is proxied too, with `ws: true`** — that is the SignalR notification hub, and it is a
separate entry because a proxy key is a path prefix, not a catch-all. Without it the browser asks
the *dev server* for the hub and gets Vite's own 404, so realtime push silently never works while
everything under `/api` keeps working normally. `ws: true` is what upgrades the connection instead
of leaving it on the long-polling fallback. Both `server` and `preview` carry both entries.

Ports are fixed (5173 dev, 4173 preview, 5234 API, 55432 the e2e Postgres) and can collide on
a busy machine. Each is overridable by environment variable: `DEV_PORT`, `PREVIEW_PORT`,
`API_PORT`, and `PG_PORT` respectively. `vite.config.ts` reads the first three;
`playwright.config.ts` and `e2e/setup/prepare-database.ts` (via the shared `e2e/support/env.ts`)
and `docker-compose.e2e.yml` read all four between them for the e2e run.

## Commands

| | |
|---|---|
| `npm start` | dev server |
| `npm run build` | production build |
| `npm test` | Vitest |
| `npm run lint` | `eslint . --max-warnings 0` |
| `npm run e2e` | Playwright, against a real API and a real Postgres |
| `npm run e2e:ui` | The same, in UI mode; keeps the database between runs |
| `npm run e2e:kind` | The deployed kind cluster |
| `npm run e2e:url -- https://…` | Any URL — including a dev loop already running on 5173 |
| `npm run e2e:report` | The last HTML report |

Lint runs with `--max-warnings 0`: one warning is a failure.

## Before the first `npm run e2e`

`scripts/install-prereqs.ps1` now installs the Playwright chromium binary, so a machine set up
through it needs nothing further here. Doing it by hand once per machine still works:

```
npx --prefix frontend playwright install chromium
```

`e2e/setup/prepare-database.ts` runs `dotnet tool restore` itself, so `dotnet-ef` (pinned in the
repo's `.config/dotnet-tools.json`) needs no separate setup step.

**It runs from the `e2e` npm script, before `playwright test` — not as Playwright's
`globalSetup`, and it must not be moved back.** Playwright starts `webServer` processes *before*
`globalSetup`, so as a global setup the database arrived too late and the API booted against
nothing. Durable Wolverine migrates its envelope schema during host startup (ADR 0005), so that
now means the API does not boot at all, and Playwright reports only "Process from
config.webServer was not able to start. Exit code: 1". Teardown stays `globalTeardown`, which
runs late by design.

See `frontend/e2e/CLAUDE.md` for the fixture and screen conventions — the fixtures, the
`@local-only` tag, and the two rules that are correctness rather than style.

## The API contract

`src/api/schema.d.ts` is **generated** from `openapi/AiFramework.Api.json` at the repo root —
never edit it. `features/orders/types.ts` is a thin set of aliases over it, which is why a
backend rename now breaks the frontend build instead of breaking it at runtime.

Regenerate after any backend contract change:

```bash
npm run generate:api
```

CI regenerates and fails on a diff, so a stale `schema.d.ts` cannot merge.

Three things about this worth knowing before you change it:

- **The generator is not a devDependency, deliberately.** Every `openapi-typescript` 7.x declares
  `peer typescript@"^5.x"` and this repo is on TypeScript 6, so `npm install` refuses.
  `--legacy-peer-deps` installs but re-resolves the tree and breaks `@testing-library/react`. It
  is a build-time CLI rather than something the app imports, so `generate:api` runs it through
  `npx --yes openapi-typescript@7.13.0` — version pinned, dependency tree untouched.
- **`quantity` is typed `number | string`, and that is correct.** ASP.NET Core's web JSON
  defaults set `AllowReadingFromString`, so the API genuinely accepts `"5"` as well as `5` and
  the document says so. Do not narrow it to `number` to make it look tidier.
- **`schema.d.ts` is in eslint's `ignores`**, beside `dist/`. It is generated output, so a style
  rule failing on it is answered by not linting it, never by relaxing the rule.
