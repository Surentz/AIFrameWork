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

Ports are fixed (5173 dev, 4173 preview, 5234 API, 55432 the e2e Postgres) and can collide on
a busy machine. Each is overridable by environment variable: `DEV_PORT`, `PREVIEW_PORT`,
`API_PORT`, and `PG_PORT` respectively. `vite.config.ts` reads the first three;
`playwright.config.ts` and `e2e/prepare-database.ts` (via the shared `e2e/env.ts`) and
`docker-compose.e2e.yml` read all four between them for the e2e run.

## Commands

| | |
|---|---|
| `npm start` | dev server |
| `npm run build` | production build |
| `npm test` | Vitest |
| `npm run lint` | `eslint . --max-warnings 0` |
| `npm run e2e` | Playwright, against a real API and a real Postgres |

Lint runs with `--max-warnings 0`: one warning is a failure.

## Before the first `npm run e2e`

`npm ci --prefix frontend` installs the `@playwright/test` package but not its browser
binary — run this once per machine:

```
npx --prefix frontend playwright install chromium
```

`e2e/prepare-database.ts` runs `dotnet tool restore` itself, so `dotnet-ef` (pinned in the repo's
`.config/dotnet-tools.json`) needs no separate setup step.

**It runs from the `e2e` npm script, before `playwright test` — not as Playwright's
`globalSetup`, and it must not be moved back.** Playwright starts `webServer` processes *before*
`globalSetup`, so as a global setup the database arrived too late and the API booted against
nothing. Durable Wolverine migrates its envelope schema during host startup (ADR 0005), so that
now means the API does not boot at all, and Playwright reports only "Process from
config.webServer was not able to start. Exit code: 1". Teardown stays `globalTeardown`, which
runs late by design.
