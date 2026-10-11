# Dependency Backlog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the five red Dependabot PRs (#67, #75, #53, #52, #51, #50) into three green,
mergeable `deps` PRs plus one `ci` PR that stops the same split from recurring.

**Architecture:** Four pull requests, one Conventional Commit type each. Three fix-ups are pushed
onto Dependabot's own branches, as the header of `.github/dependabot.yml` prescribes ("check out
its branch ... and push the result onto the PR"). The fourth is an ordinary `claude/*` branch that
changes `dependabot.yml`. Nothing here adds a feature; each task ends when its PR's seven required
checks are green.

**Tech Stack:** Quartz.NET 4.2.1, EF Core migrations (Npgsql), xUnit + FluentAssertions +
Testcontainers; npm 11 on Node 24.20.0, Vitest 5, ESLint 10, typescript-eslint 8.

**Spec:** None. This plan argues from the findings below, each verified on 2026-09-29 against
`main` at `1d607c5` in a throwaway worktree (since removed).

## Findings

| PR | Update | Why it is red | Verified fix |
|---|---|---|---|
| #67 | Quartz 4.1.0 → 4.2.1 | Quartz 4.2 refuses to start against a schema without three new nullable columns on `qrtz_triggers`. Every worker integration test fails at host start with `Database schema validation failed under table prefix 'quartz.qrtz_'` | A new EF migration adding the columns, per the `AddQuartzSchema` migration's own remarks |
| #75 | vitest 4.1.11 → 5.0.2 | `npm ci` ERESOLVE: `@vitest/coverage-v8@4.1.11` peers `vitest@4.1.11` exactly | Bump `@vitest/coverage-v8` to 5.0.2 in the same PR. Probed: 25 files / 144 tests pass, `tsc -b` and lint clean, `--coverage` runs, clean `npm ci` succeeds. No code change |
| #53 | @vitest/coverage-v8 → 5.0.1 | The mirror image of #75 | Closed as superseded by #75 |
| #52 | eslint 9.39.5 → 10.11.0 | `npm ci` ERESOLVE: `eslint-plugin-jsx-a11y@6.10.2` (latest; last release 2024-10-26) peers `eslint ^3‖…‖^9` | Bump `@eslint/js` to 10 alongside, plus an npm `overrides` entry letting jsx-a11y use the root eslint. Probed: lint clean; a file breaking one rule per plugin yields the same nine errors under 9 and 10; jsx-a11y calls none of the `context` methods ESLint 10 removed (only `context.settings`) |
| #51 | @eslint/js → 10.0.1 | Green alone, but half of #52's change | Closed as superseded by #52 |
| #50 | typescript 6.0.3 → 7.0.2 | `typescript-eslint@8.71.0` (latest) peers `typescript >=4.8.4 <6.1.0`. Upstream tracks TS 7 in typescript-eslint#10940; #12518 and #12720 were closed not-planned as duplicates; a prototype is open as #12803 | Not upgradable. Close; ignore `typescript` majors in `dependabot.yml` until typescript-eslint's peer range admits 7 |

The frontend PRs' red **`api contract is current`** is not a contract change: that job runs
`npm ci` in `frontend/` first, which fails on the same ERESOLVE. It goes green with the install.

Quartz 4.2's other changes do not reach this repository: its new analyzers built clean under
warnings-as-errors in CI (`QZ0003` needs `[PersistJobDataAfterExecution]`, which nothing uses);
execution history is off, so `add_execution_history_postgres.sql` is not needed (the release notes:
"optional: needed only with `UseExecutionHistory()`"); and 4.1.1's change making each firing its
own trace root is asserted by no test here (`JobRunRecordingTests` only asserts a trace id exists).

## Global Constraints

- One Conventional Commit type per PR: Tasks 1–3 are `deps`, Task 4 is `ci`. PR titles are
  `type(scope): summary`; `pr-labels.yml` fails any other shape.
- Fix-ups go onto Dependabot's branch (`gh pr checkout <n>`), not a new branch. Once a human
  commit is on it, Dependabot stops rebasing it — that is expected.
- **Never hand-edit an applied EF migration.** Quartz's schema change is a new migration;
  `AddQuartzSchema` is not touched. `.claude/hooks/protect-migrations.ps1` enforces this.
- `dotnet ef` uses `src/Infrastructure` as both `--project` and `--startup-project`, never `src/Api`.
- Warnings are errors, in both Debug and Release.
- Check `dotnet --list-sdks` shows 10.0.400 and `node --version` shows v24.20.0 before trusting a
  local result as evidence about CI.
- Merging is the user's step. Each task ends with a green PR handed over, not a merge.
- Tasks 2 and 3 both rewrite `frontend/package-lock.json`: **Task 3 starts only after Task 2's PR
  is merged.** Tasks 1 and 4 are independent of everything.

## Review Focus

1. **A developer's persistent dev database (port 55433) is still on the 4.1 schema after pulling.**
   The worker refuses to start, naming Quartz's own SQL scripts rather than the EF migration. The
   expected fix is `dotnet ef database update`, never `CreateIfMissing` → Task 1 updates the
   jobs skill's diagnosis row to match 4.2's wording, and says so in the PR body.
2. **The migration meets a database whose columns already exist** (someone ran Quartz's
   `add_continuations_postgres.sql` by hand, as its own error message tells them to). Expected: the
   migration succeeds → Task 1 uses `ADD COLUMN IF NOT EXISTS` and Step 7 runs it twice against
   the dev database.
3. **The columns exist but with the wrong type or in the wrong schema** (the upstream script is
   unqualified; ours must target `quartz.`) → Task 1's test pins name, type and schema, not just
   "the worker started".
4. **ESLint 10 runs clean because a plugin quietly stopped reporting**, not because the code is
   clean → Task 3 Step 3 lints a file with one violation per plugin and expects all nine errors.
5. **The next Vitest or ESLint major arrives split across two red PRs again**, and the TypeScript
   ignore outlives the reason for it → Task 4 groups each family's majors and writes the unblock
   condition beside the ignore.

---

### Task 1: Quartz 4.2.1 with its continuation columns (PR #67)

**Files:**
- Create: `src/Infrastructure/Persistence/Migrations/<timestamp>_AddQuartzContinuations.cs`
- Create: `src/Infrastructure/Persistence/Migrations/<timestamp>_AddQuartzContinuations.Designer.cs` (generated, not edited)
- Modify: `tests/Worker.IntegrationTests/Jobs/SchedulingTests.cs` (new test; class summary at lines 13–17)
- Modify: `src/Infrastructure/AiFramework.Infrastructure.csproj:88` (comment only)
- Modify: `.claude/skills/jobs/SKILL.md:56` (diagnosis row)

**Interfaces:**
- Consumes: `QuartzRegistration.TablePrefix` = `"quartz.qrtz_"`; `WorkerFactory` and
  `WorkerFactoryCollection` from `tests/Worker.IntegrationTests/WorkerFactory.cs`.
- Produces: the migration `AddQuartzContinuations`. Nothing else references it.

- [ ] **Step 1: Bring #67 onto current `main` and check it out**

```bash
gh pr comment 67 --body "@dependabot rebase"
# Wait until the head commit changes (Dependabot takes a minute or two):
gh pr view 67 --json headRefOid,mergeable --jq '"\(.headRefOid[0:7]) \(.mergeable)"'
gh pr checkout 67
git log --oneline -3   # the Dependabot commit sits on top of main's current head
```

If Dependabot declines to rebase, merge instead: `git merge origin/main` on the checked-out branch.

- [ ] **Step 2: Write the failing test**

Add to `SchedulingTests` (it already has `using Microsoft.Extensions.DependencyInjection;`; add
`using AiFramework.Infrastructure.Persistence;`, `using Microsoft.EntityFrameworkCore;` and
`using Npgsql;` if absent):

```csharp
    /// <summary>
    /// Quartz 4.2 reads and writes three continuation columns on every trigger it stores, and a
    /// 4.2 node refuses to start without them. Validate at startup already proves they exist; this
    /// pins what that cannot: that they are in the `quartz` schema this repo uses, not the
    /// unqualified table Quartz's own upgrade script names, and that their types are Quartz's.
    /// </summary>
    [Fact]
    public async Task TheTriggersTable_HasQuartz42sContinuationColumns()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AiFrameworkDbContext>();
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);

        await using var command = new NpgsqlCommand(
            "select column_name, data_type, is_nullable from information_schema.columns " +
            "where table_schema = 'quartz' and table_name = 'qrtz_triggers' " +
            "and column_name in ('continues_trigger_name', 'continues_trigger_group', 'continuation_condition') " +
            "order by column_name",
            connection);

        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                columns.Add($"{reader.GetString(0)} {reader.GetString(1)} {reader.GetString(2)}");
            }
        }

        columns.Should().Equal(
            "continuation_condition integer YES",
            "continues_trigger_group text YES",
            "continues_trigger_name text YES");
    }
```

Also update the class summary's last sentence (lines 15–16), which names a version that is about
to be wrong:

```csharp
/// all is the first assertion: Quartz runs in SchemaProvisioning.Validate, so migrations that do
/// not match the referenced Quartz package fail WorkerFactory's startup before any test body runs.
```

- [ ] **Step 3: Run it to verify it fails**

```bash
dotnet test tests/Worker.IntegrationTests --filter "FullyQualifiedName~SchedulingTests"
```

Expected: FAIL — every test in the class, at host start, with `Quartz.SchedulerException :
Database schema validation failed under table prefix 'quartz.qrtz_'`. (Docker must be running;
the factory starts Postgres and RabbitMQ containers.)

- [ ] **Step 4: Generate the migration**

```bash
dotnet tool restore
dotnet ef migrations add AddQuartzContinuations --project src/Infrastructure --startup-project src/Infrastructure
git status --short src/Infrastructure/Persistence/Migrations
```

Expected: two new files (`<timestamp>_AddQuartzContinuations.cs` and `.Designer.cs`), empty
`Up`/`Down`, and **no change** to `AiFrameworkDbContextModelSnapshot.cs` — Quartz's tables are
not in EF's model. If the snapshot changed, stop: something else in the model has drifted.

- [ ] **Step 5: Write the migration**

Replace the generated `<timestamp>_AddQuartzContinuations.cs` body (keep its timestamped file
name and the `Designer.cs` as generated):

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiFramework.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Quartz 4.2's continuation columns on <c>quartz.qrtz_triggers</c>. A 4.2 node refuses to
    /// start without them (SchemaProvisioning.Validate). ADR 0017.
    /// </summary>
    /// <remarks>
    /// Taken from Quartz's <c>database/migrations/4.2/add_continuations_postgres.sql</c> at tag
    /// v4.2.1, with two deliberate differences. The table is schema-qualified: the upstream script
    /// names an unqualified <c>qrtz_triggers</c>, and its existence checks read
    /// information_schema by table name alone, which would match any schema. And each
    /// <c>DO $$ IF NOT EXISTS ... $$</c> block is written as Postgres's own
    /// <c>ADD COLUMN IF NOT EXISTS</c>, which is the same guard: a database where someone already
    /// ran Quartz's script by hand, as Quartz's startup error tells them to, migrates cleanly.
    /// All three columns are nullable with no default, so every existing row stays valid and a
    /// 4.1 worker still running mid-rollout never reads them. The optional execution-history
    /// tables (<c>add_execution_history_postgres.sql</c>) are not added: history is off here, and
    /// Quartz validates those tables only when it is on.
    /// </remarks>
    public partial class AddQuartzContinuations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS continues_trigger_name text null;
                ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS continues_trigger_group text null;
                ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS continuation_condition integer null;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE quartz.qrtz_triggers DROP COLUMN IF EXISTS continuation_condition;
                ALTER TABLE quartz.qrtz_triggers DROP COLUMN IF EXISTS continues_trigger_group;
                ALTER TABLE quartz.qrtz_triggers DROP COLUMN IF EXISTS continues_trigger_name;
                """);
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test tests/Worker.IntegrationTests --filter "FullyQualifiedName~SchedulingTests"
dotnet test AiFramework.slnx
dotnet build AiFramework.slnx -c Release
```

Expected: the new test and every other `SchedulingTests` test PASS; the full suite passes (the
`JobDeliveryTests` and `JobRunRecordingTests` failures on #67's CI run were the same startup
error); Release builds with 0 warnings.

- [ ] **Step 7: Apply it to the dev database, and run it a second time**

Covers Review Focus 1 and 2. Needs the dev Postgres up (`docker compose up -d postgres`).

```bash
ConnectionStrings__Default='Host=localhost;Port=55433;Database=aiframework;Username=postgres;Password=postgres' \
  dotnet ef database update --project src/Infrastructure --startup-project src/Infrastructure

docker compose exec -T postgres psql -U postgres -d aiframework -c \
  "ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS continues_trigger_name text null;
   ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS continues_trigger_group text null;
   ALTER TABLE quartz.qrtz_triggers ADD COLUMN IF NOT EXISTS continuation_condition integer null;"
```

Expected: `database update` applies `AddQuartzContinuations`; the re-run prints three
`NOTICE: column "…" of relation "qrtz_triggers" already exists, skipping` and `ALTER TABLE`, no
error. Then `./scripts/worker.ps1` starts the worker and it stays up.

- [ ] **Step 8: Bring the docs to 4.2**

`src/Infrastructure/AiFramework.Infrastructure.csproj:88` — the comment names a version that is
no longer referenced:

```xml
      Quartz is the job CLOCK only: its one job type enqueues through IJobScheduler, and
```

`.claude/skills/jobs/SKILL.md:56` — Quartz 4.2's error names its own SQL scripts, which is the
wrong advice here. Replace the row with:

```markdown
| Worker refuses to start naming a `qrtz_` table, or `Database schema validation failed` naming Quartz's own `.sql` scripts | A missing migration. `dotnet ef database update` — do not run Quartz's scripts by hand and do not switch to `CreateIfMissing` |
```

Leave the `4.1.0` references in `QuartzRegistration.cs`, `EnqueueScheduledJob.cs`,
`src/Worker/CLAUDE.md` and the skill's "Checked against the 4.1.0 package" table alone: each
records what was verified against that package, and re-verifying them is not this task.

- [ ] **Step 9: Commit, push, and hand over**

```bash
git add src/Infrastructure/Persistence/Migrations tests/Worker.IntegrationTests/Jobs/SchedulingTests.cs \
  src/Infrastructure/AiFramework.Infrastructure.csproj .claude/skills/jobs/SKILL.md
git commit -m "deps: add Quartz 4.2's continuation columns in a new migration

Quartz 4.2 refuses to start against a qrtz_triggers without continues_trigger_name,
continues_trigger_group and continuation_condition. Added as a new migration from
Quartz's own add_continuations_postgres.sql, schema-qualified and idempotent.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
gh pr comment 67 --body "Adds \`AddQuartzContinuations\`: Quartz 4.2 refuses to start without three new nullable columns on \`quartz.qrtz_triggers\`. **After merging, run \`dotnet ef database update\` against the dev database before starting the worker** — Quartz's own error names its SQL scripts; do not run those. The kind deploy already migrates before any worker starts."
gh pr checks 67 --watch --interval 30
git checkout main
```

Expected: all seven required checks pass. Hand #67 to the user to merge.

---

### Task 2: Vitest 5, with its coverage provider (PR #75, closes #53)

**Files:**
- Modify: `frontend/package.json` (`@vitest/coverage-v8`)
- Modify: `frontend/package-lock.json` (regenerated by npm)

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: a lockfile Task 3 must be based on.

- [ ] **Step 1: Bring #75 onto current `main` and check it out**

```bash
gh pr comment 75 --body "@dependabot rebase"
gh pr view 75 --json headRefOid --jq '.headRefOid[0:7]'   # wait for it to change
gh pr checkout 75
node --version   # v24.20.0
```

- [ ] **Step 2: Confirm the failure it has today**

```bash
npm ci --prefix frontend
```

Expected: FAIL, `ERESOLVE` naming `peer vitest@"4.1.11" from @vitest/coverage-v8@4.1.11`.

- [ ] **Step 3: Move the coverage provider with it**

```bash
npm install --prefix frontend --no-audit --no-fund -D @vitest/coverage-v8@^5.0.2
git diff frontend/package.json
```

Expected diff in `package.json`: `"@vitest/coverage-v8": "^5.0.2"` beside Dependabot's
`"vitest": "^5.0.2"`, nothing else.

- [ ] **Step 4: Run what CI runs, from a clean install**

```bash
rm -rf frontend/node_modules
npm ci --prefix frontend
npm run lint --prefix frontend
npm run build --prefix frontend
npm test --prefix frontend -- --run
(cd frontend && npx vitest run --coverage)
```

Expected: every step exits 0. Tests: every file and test passes, and the counts match `main`'s
(25 files / 144 tests on 2026-09-29). Coverage prints its summary table. No source file changes —
if any test needs editing, stop and record why in the PR: the probe needed none.

- [ ] **Step 5: Commit, push, retitle, and close the half-PR**

```bash
git add frontend/package.json frontend/package-lock.json
git commit -m "deps: bump @vitest/coverage-v8 to 5.0.2 alongside vitest

The provider peers vitest at its exact version, so neither installs without the other.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
gh pr edit 75 --title "deps: bump vitest and @vitest/coverage-v8 to 5.0.2"
gh pr close 53 --comment "Superseded by #75, which moves vitest and @vitest/coverage-v8 together — each peers the other's exact version, so neither installs alone."
gh pr checks 75 --watch --interval 30
git checkout main
```

Expected: all seven checks pass, `api contract is current` included. Hand #75 to the user to
merge. **Task 3 waits for that merge.**

---

### Task 3: ESLint 10, with `@eslint/js` and a jsx-a11y override (PR #52, closes #51)

**Files:**
- Modify: `frontend/package.json` (`@eslint/js`, new `overrides`)
- Modify: `frontend/package-lock.json`
- Modify: `frontend/CLAUDE.md` (new section after "The API contract")

**Interfaces:**
- Consumes: `main` with Task 2 merged.
- Produces: the "Dependencies held back or overridden" section of `frontend/CLAUDE.md`, which
  Task 4 appends to.

- [ ] **Step 1: Bring #52 onto `main` (with #75 merged) and check it out**

```bash
gh pr view 75 --json state --jq .state   # MERGED, or stop
gh pr comment 52 --body "@dependabot rebase"
gh pr view 52 --json headRefOid --jq '.headRefOid[0:7]'   # wait for it to change
gh pr checkout 52
```

- [ ] **Step 2: Bump `@eslint/js` and let jsx-a11y use the root eslint**

```bash
(cd frontend && npm pkg set 'overrides.eslint-plugin-jsx-a11y.eslint=$eslint')
npm install --prefix frontend --no-audit --no-fund -D @eslint/js@^10.0.1
git diff frontend/package.json
```

Expected `package.json` diff: `"eslint": "^10.11.0"` (Dependabot's), `"@eslint/js": "^10.0.1"`,
and a new top-level block:

```json
  "overrides": {
    "eslint-plugin-jsx-a11y": {
      "eslint": "$eslint"
    }
  }
```

`$eslint` is npm's reference to the root `devDependencies` version, so the override follows
future eslint bumps without being edited.

- [ ] **Step 3: Prove every plugin still reports (Review Focus 4)**

Create `frontend/src/Probe.tsx` — **never commit it**:

```tsx
import { useState } from 'react';

export const helper = (x: any): number => x as number;

export function Probe({ on }: { on: boolean }): React.JSX.Element {
  if (on) {
    const [v] = useState(0);
    void v;
  }
  try { helper(1); } catch {}
  return <div><img src="a.png" /><div onClick={() => {}} /></div>;
}
```

```bash
cd frontend && npx eslint src/Probe.tsx; rm src/Probe.tsx; cd ..
```

Expected: exactly these nine rule ids, the same set ESLint 9 reports for this file:
`react-refresh/only-export-components`, `@typescript-eslint/no-explicit-any`,
`react-hooks/rules-of-hooks`, `@typescript-eslint/no-meaningless-void-operator`, `no-empty`,
`jsx-a11y/alt-text`, `jsx-a11y/click-events-have-key-events`,
`jsx-a11y/no-static-element-interactions`, `@typescript-eslint/no-empty-function`. A missing
id means that plugin stopped reporting under ESLint 10: stop, and do not land the override.
Confirm `git status` shows no `Probe.tsx`.

- [ ] **Step 4: Run what CI runs, from a clean install**

```bash
rm -rf frontend/node_modules
npm ci --prefix frontend
npm run lint --prefix frontend
npm run build --prefix frontend
npm test --prefix frontend -- --run
```

Expected: all exit 0; `npm ci` prints no `ERESOLVE`.

- [ ] **Step 5: Record the override where the next person will look**

Add this section to `frontend/CLAUDE.md`, directly after "## The API contract" and its bullets:

```markdown
## Dependencies held back or overridden

Each entry says what would let it go. Check before assuming a peer-range error is new.

- **`eslint-plugin-jsx-a11y` runs under an npm `overrides` entry** (`package.json`) that points
  its `eslint` peer at the root version. 6.10.2 is the latest release (October 2024) and declares
  `eslint ^3‖…‖^9`, so without the override `npm ci` refuses ESLint 10. It calls none of the rule
  `context` methods ESLint 10 removed, and a file breaking one rule per plugin reports the same
  errors under 9 and 10 (verified 2026-09-29). **Remove the override** once a jsx-a11y release
  declares ESLint 10 — upstream issues jsx-eslint/eslint-plugin-jsx-a11y#1075, #1079, #1081.
```

- [ ] **Step 6: Commit, push, retitle, and close the half-PR**

```bash
git add frontend/package.json frontend/package-lock.json frontend/CLAUDE.md
git commit -m "deps: move @eslint/js to 10 with eslint, and override jsx-a11y's eslint peer

eslint-plugin-jsx-a11y 6.10.2 is the latest release and still declares eslint <=9. It
uses none of the context APIs ESLint 10 removed, and reports identically under both.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
gh pr edit 52 --title "deps: bump eslint and @eslint/js to 10"
gh pr close 51 --comment "Superseded by #52, which moves eslint and @eslint/js together."
gh pr checks 52 --watch --interval 30
git checkout main
```

Expected: all seven checks pass. Hand #52 to the user to merge.

---

### Task 4: Keep each family's majors together, and hold TypeScript 7 (closes #50)

**Files:**
- Modify: `.github/dependabot.yml` (the `npm` entry)
- Modify: `frontend/CLAUDE.md` ("Dependencies held back or overridden", created in Task 3 — if
  Task 3 has not merged yet, create the section with its heading and intro line from Task 3
  Step 5, and only this task's bullet)

**Interfaces:**
- Consumes: nothing from other tasks' code.
- Produces: nothing other tasks use.

- [ ] **Step 1: Branch from `main`**

```bash
git checkout main && git pull --ff-only
git checkout -b claude/dependabot-frontend-families
```

- [ ] **Step 2: Rewrite the `npm` entry's `groups` and `ignore`**

In `.github/dependabot.yml`, replace the `npm` entry's `groups:` and `ignore:` blocks with:

```yaml
    groups:
      # Families whose packages peer each other's exact or same-major version, so a major for one
      # alone can never install: `npm ci` fails ERESOLVE and the PR is red for a reason Dependabot
      # cannot fix. Grouped with no update-types, so majors arrive as one PR per family. Listed
      # before `frontend`: Dependabot places a dependency in the first group it matches.
      vitest:
        patterns: ['vitest', '@vitest/*']
      eslint:
        patterns: ['eslint', '@eslint/*']
      # Majors outside those families stay one PR per package: those are the ones that need reading.
      frontend:
        update-types: [minor, patch]
    ignore:
      # LTS only: @types/node follows the Node major CI runs on, which moves by hand.
      - dependency-name: '@types/node'
        update-types: [version-update:semver-major]
      # typescript-eslint (8.71.0) peers typescript <6.1.0, and typed linting needs a TypeScript
      # JS API that 7 does not ship yet (typescript-eslint#10940). Lift this once a
      # typescript-eslint release's peer range admits 7. frontend/CLAUDE.md lists it.
      - dependency-name: typescript
        update-types: [version-update:semver-major]
```

- [ ] **Step 3: Check it parses and says what it should**

```bash
npx --yes js-yaml .github/dependabot.yml > /dev/null && echo parses
git diff .github/dependabot.yml
```

Expected: `parses`; the diff touches only the `npm` entry. (GitHub validates the file itself
after merge — Insights → Dependency graph → Dependabot shows any error.)

- [ ] **Step 4: Record the hold in `frontend/CLAUDE.md`**

Append to "## Dependencies held back or overridden":

```markdown
- **TypeScript stays on 6.x.** `typescript-eslint` 8.71.0 peers `typescript <6.1.0`: typed
  linting needs a TypeScript JS API that 7 does not ship yet (typescript-eslint#10940; a
  prototype is typescript-eslint#12803). `dependabot.yml` ignores `typescript` majors for this
  reason. **Lift both** once a typescript-eslint release's peer range admits 7 — and check
  `openapi-typescript`'s peer range at the same time (see "The API contract").
```

- [ ] **Step 5: Commit, push, open the PR, and close #50**

```bash
git add .github/dependabot.yml frontend/CLAUDE.md
git commit -m "ci: group vitest and eslint majors, and hold TypeScript 7

Two families arrived as four red PRs because each package peers the other's major. And
typescript-eslint does not yet accept TypeScript 7, so its major is ignored until it does.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push -u origin claude/dependabot-frontend-families
gh pr create --title "ci: group vitest and eslint majors, and hold TypeScript 7" --body "$(cat <<'EOF'
- **Groups** `vitest` + `@vitest/*` and `eslint` + `@eslint/*` with no `update-types`, ahead of `frontend`, so each family's major arrives as one PR. #75/#53 and #52/#51 were each one change split into two red PRs.
- **Ignores `typescript` majors.** typescript-eslint 8.71.0 peers `typescript <6.1.0` (typescript-eslint#10940). The condition to lift it is written beside the rule and in `frontend/CLAUDE.md`.

Closes #50.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
gh pr close 50 --comment "Blocked upstream: typescript-eslint 8.71.0 peers \`typescript <6.1.0\` (typescript-eslint#10940). TypeScript majors are now ignored in \`dependabot.yml\` until that changes — see the ci PR."
gh pr checks --watch --interval 30
git checkout main
```

Expected: all seven checks pass, the PR is labelled `ci` by `pr-labels.yml`. Hand it to the user
to merge.
