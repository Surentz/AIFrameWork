# Frontend

Vite + React workspace, TypeScript, client-side SPA.

## TypeScript strictness

`tsconfig.app.json` and `tsconfig.node.json` both carry the repo's strictness delta — `strict`,
`noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`, `noImplicitOverride`,
`noFallthroughCasesInSwitch`, `noImplicitReturns`. Keep the two files in step: the node config is
what gives `e2e/**` and the config files themselves the same checks. Never relax a flag to make an
error go away.

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

Ports are fixed (5173 dev, 4173 preview, 5234 API, 5235 the job worker, 55432 the e2e Postgres,
55682/55683 the e2e RabbitMQ's AMQP and management UI, 55692/55693 the e2e partner simulator) and can collide on a busy machine. Each is
overridable by environment variable: `DEV_PORT`, `PREVIEW_PORT`, `API_PORT`, `WORKER_PORT`,
`PG_PORT`, `E2E_RABBITMQ_PORT`, `E2E_RABBITMQ_UI_PORT`, `SIMULATOR_PORT` and `SIMULATOR_HEALTH_PORT` respectively. `vite.config.ts` reads the
first three; `playwright.config.ts` and `e2e/setup/prepare-database.ts` (via the shared
`e2e/support/env.ts`) and `docker-compose.e2e.yml` read the rest between them for the e2e run.

## Commands

| | |
|---|---|
| `npm start` | dev server |
| `npm run build` | production build |
| `npm run preview` | serve the production build on 4173 |
| `npm test` | Vitest |
| `npm run lint` | `eslint . --max-warnings 0` |
| `npm run format` | Prettier, writing in place |
| `npm run generate:api` | regenerate `src/api/schema.d.ts` — see "The API contract" below |
| `npm run e2e` | Playwright, against a real API, job worker and Postgres |
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
repo's `.config/dotnet-tools.json`) needs no separate setup step. It also builds the API and the
worker, which Playwright then starts with `--no-build` — see `e2e/CLAUDE.md`.

**It runs from the `e2e` npm script, before `playwright test` — not as Playwright's
`globalSetup`, and it must not be moved back.** Playwright starts `webServer` processes *before*
`globalSetup`, so as a global setup the database arrived too late and the API booted against
nothing. Durable Wolverine migrates its envelope schema during host startup (ADR 0005), so that
now means the API does not boot at all, and Playwright reports only "Process from
config.webServer was not able to start. Exit code: 1". Teardown is not `globalTeardown` either:
that runs *before* the webServers stop, so it pulled the database from under a live API and
worker. `run.ts` runs `e2e/setup/teardown-database.ts` after Playwright has exited.

The API and worker log at **Warning** during a run (`E2E_SERVER_LOG_LEVEL`, default `Warning`).
At their own Information default EF Core prints every SQL command — about twelve thousand lines
a run. Set `E2E_SERVER_LOG_LEVEL=Information` to see it all while debugging.

See `frontend/e2e/CLAUDE.md` for the fixture and screen conventions — the fixtures, the
`@local-only` tag, and the two rules that are correctness rather than style.

## The API contract

`src/api/schema.d.ts` is **generated** from `openapi/AiFramework.Api.json` at the repo root —
never edit it. Each feature's `types.ts` (`features/orders/types.ts` is the model) is a thin set
of aliases over it, which is why a backend rename now breaks the frontend build instead of
breaking it at runtime.

Administrator screens (`/fulfilment`, `/monitoring/*`, the catalogue's create and edit forms) sit
behind `RequireRole allow="Admin"` in `routes.tsx`. That only hides UI — the API's capability
policies are what refuse the request.

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

## The export viewer

`/orders/exports` reads a PDF with react-pdf (pdf.js) inside a native `<dialog>` (ADR 0030).

- **`ExportViewer.tsx` is lazy-loaded** (`ExportViewerDialog.tsx`), so pdf.js — about 1 MB — loads only
  on the first View. Do not import `react-pdf` anywhere else, or it lands in the main bundle.
- **`workerSrc` is set in `ExportViewer.tsx`**, the module that renders `<Document>`: react-pdf warns that
  one set elsewhere can be overwritten by its default. The worker is bundled and same-origin.
- **The worker is `.mjs`, which nginx does not map by default.** `nginx.conf` maps it under `/assets/`;
  without that the viewer never draws in the cluster while working in dev and e2e (both run Vite).
- **pdf.js detaches the buffer it is given**, so the viewer hands it a copy. Passing the query's cached
  `ArrayBuffer` breaks the second open.
- **Unit tests stub `react-pdf`** (pdf.js cannot draw in jsdom) and `src/test/setup.ts` stubs `<dialog>`
  (jsdom has none of its behaviour). Drawing, Esc and focus return are tested in Playwright.

## Dependencies held back or overridden

Each entry says what would let it go. Check before assuming a peer-range error is new.

- **TypeScript stays on 6.x.** `typescript-eslint` 8.71.0 peers `typescript <6.1.0`: typed
  linting needs a TypeScript JS API that 7 does not ship yet (typescript-eslint#10940; a
  prototype is typescript-eslint#12803). `dependabot.yml` ignores `typescript` majors for this
  reason. **Lift both** once a typescript-eslint release's peer range admits 7 — and check
  `openapi-typescript`'s peer range at the same time (see "The API contract").
- **`eslint-plugin-jsx-a11y` runs under an npm `overrides` entry** (`package.json`) that points
  its `eslint` peer at the root version. 6.10.2 is the latest release (October 2024) and declares
  `eslint ^3‖…‖^9`, so without the override `npm ci` refuses ESLint 10 — and Dependabot's
  `eslint` group cannot fix that, because jsx-a11y is not in it. It calls none of the rule
  `context` methods ESLint 10 removed, and still reports its violations under 10 (verified
  2026-10-06 on 10.12.0). **Remove the override** once a jsx-a11y release declares ESLint 10 —
  upstream issues jsx-eslint/eslint-plugin-jsx-a11y#1075, #1079, #1081.
