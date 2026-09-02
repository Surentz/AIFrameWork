# React UI: replacing the Angular stack

**Date:** 2026-09-02
**Status:** Approved, not yet implemented
**Supersedes the frontend half of:** `2026-08-27-claude-framework-design.md`

## Why

The repo was scaffolded with Angular tooling — two skills, a reviewer agent, an
`/ng-feature` command, a pre-written eslint config, and a `frontend/CLAUDE.md` full of
Angular conventions. None of it was ever exercised: `frontend/` holds exactly two files and
the workspace was never created. The founding design spec's deferred list still has
"scaffold the Angular workspace" unchecked.

Switching to React now costs configuration and documentation. Switching later costs
application code. That asymmetry is the whole argument for doing it in this phase.

## Decisions

| Decision | Choice | Rejected |
|---|---|---|
| Stack | Vite + React + TypeScript, SPA | Next.js — its server half overlaps the .NET API and forces an auth and data-fetching split that does not need to exist |
| Routing | React Router | TanStack Router — better types, smaller ecosystem; not worth the unfamiliarity on a first slice |
| Server state | TanStack Query | Hand-rolled fetch in effects |
| Tests | Vitest + React Testing Library + MSW, plus Playwright e2e | Module-level mocking without MSW — the seam sits above `fetch`, so wrong URLs and verbs pass |
| Scope | Tooling swap, workspace, and one real Orders slice | Tooling swap alone — leaves the toolchain unproven |

Vitest was already the named test runner in `frontend/CLAUDE.md`, so it carries over
unchanged.

## 1. Retiring Angular

Deleted and replaced under new names, because a file called `angular-conventions`
containing React rules outlives everyone's memory of why. Git history preserves the
originals.

| Deleted | Replacement |
|---|---|
| `.claude/skills/angular-conventions/SKILL.md` | `.claude/skills/react-conventions/SKILL.md` |
| `.claude/skills/angular-testing/SKILL.md` | `.claude/skills/react-testing/SKILL.md` |
| `.claude/commands/ng-feature.md` | `.claude/commands/react-feature.md` |
| `.claude/agents/angular-reviewer.md` | `.claude/agents/react-reviewer.md` |

Rewritten in place:

- **`frontend/CLAUDE.md`** — the `tsconfig` delta survives almost verbatim; `strict`,
  `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`, `noImplicitOverride`,
  `noFallthroughCasesInSwitch` and `noImplicitReturns` are TypeScript settings, not Angular
  ones. Only the `angularCompilerOptions` block goes. The command table survives, with
  `lint` changing from `ng lint --max-warnings 0` to `eslint . --max-warnings 0`.
- **`frontend/eslint.config.js`** — drops `angular-eslint`, `processInlineTemplates`, the
  `**/*.html` block, and the selector rules. Keeps `tseslint.configs.strictTypeChecked`,
  `stylisticTypeChecked`, every rule in the "Type safety" group, and `no-empty`. Adds
  `eslint-plugin-react-hooks`, `eslint-plugin-jsx-a11y`, and `eslint-plugin-react-refresh`.
  Converts to ESM, because Vite's `package.json` sets `"type": "module"` — the file's own
  header comment already anticipated this.
- **`.prettierrc`** — the `*.html` override to `parser: "angular"` is deleted. React has no
  separate template files.

Small edits: root `CLAUDE.md` (versions, layout, commands, "More context"),
`.prettierignore`, `.gitignore`, `.claude/hooks/format-and-lint.ps1` (add `.tsx` and `.jsx`
to its extension list; one message names the retired skill), and `.claude/commands/verify.md`
(the lint line, plus a new step 4).

**One capability is genuinely lost.** `angular-eslint`'s `templateAccessibility` config
linted templates for accessibility. Its React counterpart, `jsx-a11y`, is weaker: it sees
JSX only, not runtime composition. This is a reduction, recorded here rather than left to be
discovered.

**`docs/superpowers/` is not edited.** Those files are dated records of decisions actually
made; rewriting them to say "React" would falsify the history. The switch is recorded
forward as **ADR 0004: React over Angular**, following the amendment precedent ADR 0003
established.

## 2. Backend: `GET /api/orders`

The API currently exposes only `POST /api/orders` and `GET /api/orders/{id:guid}`. A list
view has no endpoint behind it, so this phase adds one.

**Domain is untouched.** No new entity, no new property.

**One migration, for an index.** `AddOrderPlacedAtIndex` on `(PlacedAt DESC, Id DESC)`.
Without it, every page sorts the whole table. A new migration — never an edit to the two
applied ones.

**Keyset paging, not offset.** Orders are append-only and the list is newest-first, which is
precisely the shape offset paging gets wrong: a row inserted while a user sits on page 1
makes page 2 repeat an item that shifted down. Keyset costs one extra disjunct in the
predicate and removes the bug class.

```csharp
// src/Application/Orders/GetOrders.cs
public sealed record OrderListItem(Guid Id, string Sku, int Quantity, DateTimeOffset PlacedAt);
public sealed record OrderPage(IReadOnlyList<OrderListItem> Items, string? NextCursor);
public sealed record GetOrders(int Limit, string? Cursor) : IQuery<OrderPage>;
```

The cursor is an opaque base64 encoding of `PlacedAt|Id`. Clients never parse it.

**Validation lives in the handler.** `QueryDispatcher` does not run the validation or
unit-of-work behaviours — those are command-only, verified by reading
`src/Infrastructure/Messaging/Dispatchers.cs`. So `GetOrdersHandler` checks its own inputs: a
`Limit` outside `1..100` and a malformed cursor both return
`Result.Failure(ErrorKind.Validation, …)`, which `ResultExtensions.Problem` renders as a 400
`ProblemDetails`. A garbage cursor must not become a 500.

**Repository** gains one method, mirroring `GetAsync`'s existing style:

```csharp
Task<IReadOnlyList<Order>> ListAsync(
    int limit, (DateTimeOffset PlacedAt, Guid Id)? after, CancellationToken cancellationToken);
```

`AsNoTracking()`, ordered by `(PlacedAt DESC, Id DESC)`, `Take(limit + 1)` so "is there a next
page" is answered without a second `COUNT`.

**Api** gains a bare `[HttpGet]` action beside the existing `[HttpGet("{id:guid}")]`. No route
conflict, and `CreatedAtAction(nameof(Get), …)` keeps working.

### Test requirements

One per layer: handler behaviour in `Application.Tests`, the SQL in `Infrastructure.Tests`
against Postgres, the wire shape in `Api.IntegrationTests`.

**The pagination test must insert a new order between fetching page 1 and page 2**, and assert
page 2 neither repeats nor skips a row. A test that pages a static table passes identically
under correct keyset paging and under broken offset paging — it cannot fail for the reason its
name claims. Seven tests on the preceding branch had exactly this defect, and every one was
caught by review or mutation rather than by the suite going red. This one is specified up
front.

## 3. The React application

```
frontend/
  vite.config.ts        proxy /api -> http://localhost:5234
  src/
    api/client.ts       typed fetch; parses ProblemDetails into a typed ApiError
    api/orders.ts       listOrders / getOrder / placeOrder
    features/orders/    OrderList, OrderDetail, PlaceOrderForm, queries.ts
    test/               MSW handlers + setup
  e2e/orders.spec.ts
```

Routes: `/orders` (list), `/orders/:id` (detail), `/orders/new` (form).

**No CORS configuration is needed.** The Vite dev proxy makes development same-origin, and
production is a static bundle. `Program.cs` is untouched beyond section 2.

**The validation payoff.** `Behaviors.ValidateAsync` already groups messages by `PropertyName`
in the shape `ValidationProblemDetails.Errors` uses — its own comment says this exists so a
client can map a message back to a form field. Nothing has ever consumed it. `PlaceOrderForm`
is the first thing to: a 400 renders under the offending input rather than as a banner.

### Component tests

Vitest and React Testing Library, with MSW intercepting at the HTTP boundary so the real
`fetch` and TanStack Query paths execute against scripted responses — including 400
`ProblemDetails` and 404.

**MSW runs with `onUnhandledRequest: 'error'`.** Without it, a component requesting the wrong
URL falls through unmocked and the test still passes. With it, a wrong path or verb is a
failure. This is the same anti-vacuous concern as the pagination test, one layer up.

### End-to-end tests

Playwright, driving a real browser against a real API on a real Postgres — the frontend
counterpart to `Api.IntegrationTests`, catching contract drift that MSW by construction
cannot.

1. `docker-compose.e2e.yml` runs `postgres:17-alpine` — the same image the Testcontainers
   suites already pull — on port **55432**, chosen so it cannot collide with a local
   development Postgres.
2. Playwright global setup runs `docker compose up -d --wait`, then `dotnet ef database
   update` with `ConnectionStrings__Default` overridden by environment variable. `Program.cs`
   does not migrate on startup, so this step is required, not belt-and-braces.
3. `webServer: [ dotnet run --launch-profile http, vite preview ]`, each with a `url`
   Playwright polls before starting. The API's is the existing `/health` endpoint — a real
   readiness check, not a fixed wait.
4. Teardown drops the container.

The `http` launch profile is used deliberately: HTTP on `localhost:5234` avoids the
development HTTPS certificate entirely.

### Risks, named rather than discovered

- **End-to-end tests must never assert on outbox side effects.** The audit row is written by
  background workers on their own schedule; any e2e that checks for it is racy by
  construction. Assert only synchronous API state — the order appearing in the list. Should
  the audit ever need asserting, it is `expect.poll`, never a fixed wait.
- **A second flake surface.** The Testcontainers flake is a confirmed pattern in this repo
  (three occurrences, always at container startup, never an assertion failure). Playwright
  adds another. `/verify`'s step 4 inherits the existing doctrine: no Docker means **skipped,
  and said so** — never reported as a pass.
- **Fixed ports** (5234 API, 4173 preview, 55432 Postgres) can collide on a busy machine.
  Documented in `frontend/CLAUDE.md` and overridable by environment variable.

**Rejected:** driving the API in-process via `WebApplicationFactory`. Playwright is Node-side
and cannot reach it, and there would be no stable URL for the browser.

## Out of scope

Authentication and authorisation. Styling beyond what legibility requires. Any second feature
slice. A production hosting story for the static bundle.

## Definition of done

- No file describes Angular as the current stack. The historical records under
  `docs/superpowers/` and the ADR recording the switch are the only places the word survives.
- `npm run lint --prefix frontend` clean at `--max-warnings 0`.
- `npm test --prefix frontend -- --run` green, with MSW erroring on unhandled requests.
- `dotnet build` clean at zero warnings; `dotnet test` green, including the new `GetOrders`
  coverage at all three layers.
- Playwright places an order through the browser and sees it in the list.
- `/verify` reports all four steps, naming any skipped step as skipped.
- ADR 0004 recorded.
