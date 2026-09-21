# User Management Implementation Plan

An in-app screen for administering accounts: who holds the administrator role, and revoking
someone's live sessions. This is the feature ADR 0020 named in its own alternatives section —
*"not rejected on the merits — it is the right answer eventually, and it is what removes the
deploy-to-revoke cost"* — and the trigger it recorded for revisiting that refusal has now been
hit in practice.

**This plan changes ADR 0020 rather than extending it.** Phase 1 is not a convenience; it is the
prerequisite that makes the rest of the feature possible at all.

## The problem

`AdminReconciler` runs at every API start and makes the stored roles match `Admin__Usernames`
exactly: promote everyone listed, **demote every administrator who is not**. That declarative
property is what makes revocation-by-removing-a-name work today, and it is precisely what would
silently undo every grant made through a UI — at the next deploy, with no error and no log that
names the cause.

So a screen that grants the role cannot be built on top of the current reconciler. One of the two
has to give.

## Decisions taken

| Decision | Choice |
|---|---|
| Configuration's role after this | **Seeds, never demotes.** Startup still promotes everyone listed; it demotes nobody |
| Operations | **Promote / demote**, and **force sign-out everywhere** aimed at another user |
| Audit | **A dedicated table**, in the shape of `sign_in_events` |
| Rails | Refuse self-demotion; refuse removing the last administrator; confirm by name |
| Where it lives | `/monitoring/users`, inside the existing `Monitoring` policy |

### Deliberately out of scope

- **Unlocking a locked account.** Ruled out as a monitoring operator action earlier and left out
  again here. The lockout is fifteen minutes and self-clearing (ADR 0008); a button that shortens
  it is a button that weakens it.
- **Deactivating or deleting an account.** A new domain concept, a migration, and a change to the
  session validator — and nothing has asked for it yet. Force-sign-out plus demotion covers the
  urgent case ("this person must stop having access now") without inventing a lifecycle.
- **Resetting another user's password.** Puts an administrator in the credential path and creates
  an impersonation route that the audit can record but not prevent. If it is ever wanted, it wants
  its own decision.
- **A grant or permission model.** ADR 0020 declined it as speculative and nothing here changes
  that. The role stays two values.
- **Step-up authentication.** Considered and not taken; the rails below are server-side rules
  rather than friction.

### Where it lives, and why that is not a new decision

`/monitoring/users`, behind `[Authorize(Policy = AuthorizationPolicies.Monitoring)]` — the policy
that already exists. Administering users is an operations task, the only privileged surface in
this application is the monitoring page, and a second policy would be a second thing to get wrong
for no benefit. The nav entry is the existing Monitoring one; this is a sub-page like jobs,
logins and traffic.

## The trap this decision creates, stated once, loudly

**Removing a name from `Admin__Usernames` no longer revokes anything.** Revocation is now a UI
action, and configuration is a *floor* rather than a mirror.

The sharp edge follows directly: if you demote someone in the UI **while their name is still in
configuration**, the next API start promotes them straight back. Silently. It will look like the
demotion "didn't save".

> **To remove an administrator: demote them in the UI *and* remove them from `Admin__Usernames`.**
> Either alone is insufficient — the UI alone is undone at the next restart, and config alone does
> nothing at all now.

This is the price of keeping a break-glass path, and it was chosen with that price visible. The
mitigations in Phase 2 are: the reconciler logs every promotion it performs at `Information`
naming the user, and the users screen marks any account whose role is currently backed by
configuration, so the conflict is visible *before* someone tries to demote them rather than after
a restart undoes it.

## Global constraints

Everything in the repo's root `CLAUDE.md` applies. The ones this feature will actually collide
with:

- **Warnings are errors.** MA0051's 60-line limit has bitten every phase of the monitoring work.
- **Nothing on the authorization path is cacheable.** The user list and the role read are not
  `ICacheable`, permanently and for the same reason ADR 0008's lockout state is not: a stale
  answer about privilege is a security bug, not a stale read.
- **Every command and query registered** in `AddMessaging()`, with the completeness test standing
  in for compile-time safety.
- **A role change does NOT rotate the security stamp**, and must not. ADR 0020 settled this and
  this feature makes it *more* obviously right: the role is read from the database on every
  authenticated request, so a demotion takes effect on the target's very next request without
  signing them out of a session they remain entitled to hold. Force-sign-out is the separate,
  deliberate action — never a side effect of demotion.
- **Regenerate the contract** after the controller lands; `codegen write` only if a job handler
  changes (Phase 2's prune job does).

---

## Phase 1 — Configuration seeds, and stops demoting

The whole phase is a subtraction, and it ships on its own.

### Tasks

- [x] **1.1** `ReconcileAdministratorsHandler` promotes only. Drop the demotion branch; the
      candidate query no longer needs to fetch current administrators who are absent from the
      list, only the listed names.
- [x] **1.2** `AdministratorReconciliation` loses `Demoted`. It becomes `(Promoted, Unknown)` —
      keep `Unknown`, which is the typo-catcher and is now the *only* diagnostic the reconcile
      emits about a name that will never match.
- [x] **1.3** `AdminReconciler` logs each promotion at `Information` naming the user, not just a
      count. With configuration now a floor, "why is this person an administrator again" is a
      question someone will ask, and the answer must be greppable.
- [x] **1.4** Invert `ReconcileAdministratorsHandlerTests`. Its `Demoted.Should().Be(1)` case
      becomes the assertion that an administrator absent from the list is **left alone**. That
      test is the one that encodes the whole decision; name it for the rule, not the method.
- [x] **1.5** ADR 0022, superseding ADR 0020's "Administrators come from configuration, reconciled
      at startup" section. ADR 0020 stays correct about everything else.
- [x] **1.6** Root `CLAUDE.md`'s administrator section: replace the "promotion made by
      hand-written SQL is reverted" bullet, which stops being true, with the two-step revocation
      trap above.

### Traps

- **`AdministratorDirectory`'s own doc comment asserts the invariant that is changing.** It says
  registration and the reconciler must agree "or a user would be promoted by one path and demoted
  by the other on the next restart." Under promote-only they still agree, but the *reason* is
  different. Fix the comment; a stale explanation of a safety property is worse than none.
- **This is the one phase that can silently grant standing access.** Before: an administrator
  removed from config lost the role at the next restart. After: they keep it until someone acts.
  Any account currently holding `Admin` that nobody intends to be an administrator becomes
  permanent on deploy. **Check the `users` table for existing `Admin` rows before shipping this.**

### What changed during implementation

- **The candidate query narrowing turned out to be half the mechanism, not a tidy-up.**
  `ListForRoleReconciliationAsync` no longer loads current administrators, so the handler never
  even sees an account it might have demoted. The test deliberately feeds it one anyway — a
  substitute returns the absent administrator regardless of arguments — so it proves the
  *handler's* guarantee rather than only the query's. Either alone would be a weaker promise.
- **A `foreach` survived S3267 with a justified pragma.** The analyzer wanted
  `candidates.Where(u => u.ChangeRole(...))`, which puts a mutation inside a filter: promotion
  would become a side effect of a predicate, happening only when something materialises the
  sequence, and a later `.Take()` would silently promote nobody. The condition reads as a filter
  and is not one, so the loop stayed.
- **The promotion log became one event per user rather than one joined line**, and that was
  forced by CA1873 before it was chosen on merit. `string.Join` at the call site is evaluated
  before `LoggerMessage`'s own `IsEnabled` check, and the analyzer rejects a compound
  `IsEnabled(...) && count > 0` guard. Logging per user removes the argument entirely — and gives
  `{Username}` as a filterable structured property (ADR 0015) instead of a comma-separated blob
  no log store can query. The analyzer flagged `Information` and not the `Warning` beside it,
  because Information is a level people actually turn off.
- **Three doc comments asserted the invariant that was being inverted**, and all three were
  wrong in a way that would have outlived the change: `AdminOptions` ("an administrator appointed
  by hand-written SQL is demoted at the next API start" — now exactly false),
  `IUserRepository.ListForRoleReconciliationAsync`, and `AdministratorDirectoryTests`' own
  summary. A stale explanation of a safety property is worse than none.
- **The inverted test was mutation-checked**, not merely observed to pass: restoring the demotion
  branch turns exactly that one test red, and nothing else.

---

## Phase 2 — The commands, the rails, and the audit

### Data model

`admin_actions`, one row per administrative change:

| Column | Notes |
|---|---|
| `Id` | |
| `OccurredAt` | |
| `ActorUserId`, `ActorUsername` | The username is **denormalized on purpose** — the record must stay readable regardless of what later happens to the actor's account |
| `TargetUserId`, `TargetUsername` | Same |
| `Action` | `Promoted`, `Demoted`, `SignedOutEverywhere` |
| `IpAddress`, `UserAgent` | From the existing `IClientContext` (Phase 3 of the monitoring plan) |
| `TraceId` | Pivots to the request's own logs, exactly as `sign_in_events` does |

Indexed on `(OccurredAt DESC)`, and on `TargetUserId` so "what has been done to this account" is a
seek rather than a scan.

### Tasks

- [x] **2.1** `ListUsers(search, page, size)` → `UserPage`, mirroring `SignInEventPage`. Username,
      display name, role, `LastSeenAt`, registered-at, and **`RoleIsConfigured`** — whether this
      account's name appears in `Admin__Usernames`, read from `IAdministratorDirectory`. That last
      field is what makes the Phase 1 trap visible in the UI instead of after a restart.
- [x] **2.2** `ChangeUserRole(TargetUserId, UserRole)` command, carrying both rails:
      - **self-demotion** — compare against `ICurrentUser.Id` and refuse with a `Conflict`;
      - **last administrator** — see the trap below; this is not a read-then-write.
- [x] **2.3** `SignOutUser(TargetUserId)` — rotates the target's security stamp through the
      existing `User.RotateSecurityStamp`. A separate command from `SignOutEverywhere`, which is
      self-aimed; sharing one command would mean one authorization rule for two very different
      acts.
- [x] **2.4** `IAdminAudit` port + `AdminAudit` adapter + `AdminAction` entity and configuration,
      in the shape `ISignInAudit`/`SignInAudit` already established. Every one of the three
      actions writes exactly one row, **in the same `SaveChangesAsync` as the change itself** —
      an audit that can be committed without its effect, or vice versa, is not an audit.
- [x] **2.5** Migration `AddAdminActions`.
- [x] **2.6** `MonitoringUsersController` — `GET /api/monitoring/users`,
      `POST /api/monitoring/users/{id}/role`, `POST /api/monitoring/users/{id}/sign-out`,
      `GET /api/monitoring/users/{id}/actions`. Same policy as every other monitoring route.
- [x] **2.7** `PruneAdminActions` job + `Monitoring__AdminActionRetentionDays`, registered in
      `JobRegistration` and scheduled daily beside `PruneSignInEvents`. **Regenerate the worker's
      codegen tree.**
- [x] **2.8** Tests: both rails including the race, the audit-and-change atomicity, the
      self-demotion refusal, and that demotion does **not** rotate the target's stamp.
- [x] **2.9** Regenerate the API contract.

### Traps

- **The last-administrator check must not be read-then-write** — and, as it turned out, putting
  the condition in the `UPDATE`'s own `WHERE` clause is *not on its own sufficient either*. That
  was the plan's original instruction and it was wrong; see Phase 2's "What changed" below. A
  subquery read takes no locks, so two overlapping transactions demoting different administrators
  both pass the `EXISTS` and the table ends with nobody. The implementation needs the conditional
  statement **and** a transaction-scoped advisory lock that serialises demotions.
- **`ICurrentUser.Id` memoizes** (ADR 0009). That is what makes the self-demotion check safe to
  read inside a handler, but it also means a test substituting it must set it before dispatch.
- **`RoleIsConfigured` is a per-row call into options, not a database join.** `AdminOptions` is a
  configured list, not a table — do not attempt to express it in the query. Read the list once per
  request and match in memory against the page's rows.
- **The audit is personal data, like `sign_in_events`.** It holds an address and a user-agent
  against two usernames. The prune job is a requirement of the feature, not housekeeping.

### Open decision for this phase

**Retention is set to thirty days to match `sign_in_events`, and I would argue for longer.** The
two tables answer different questions: sign-in events are high-volume operational noise where
thirty days is generous, while a privilege change is rare, deliberate, and exactly the record you
want when reconstructing how someone came to have access six months ago. The knob is
`Monitoring__AdminActionRetentionDays` and it is independent of the sign-in one precisely so this
can be revisited without touching the other. **Settle this before 2.7 lands** — raising it later
does not recover rows already deleted.

### What changed during implementation

- **The last-administrator rail took three attempts, and the first two passed their tests while
  being wrong.** This is the finding worth carrying forward from this phase.

  The plan said: put the condition in the `UPDATE`'s own `WHERE` clause, "no read to race
  against." That closes the read-then-write gap and nothing else. A subquery read takes **no
  locks**, so two *overlapping transactions* demoting *different* administrators each miss the
  other's uncommitted change under READ COMMITTED, both pass the `EXISTS`, and the table ends
  with zero administrators. Demonstrated against real Postgres.

  Worse, the tests written for it had no teeth. A two-way `Task.WhenAll` race passed against the
  broken implementation; so did an eight-way one. EF and Npgsql serialise concurrent calls in
  practice, so the interleaving cannot be produced on demand, and **a race test that cannot
  observe the race is worse than no test** — it converts an open question into false confidence.

  The fix is a transaction-scoped advisory lock (`pg_advisory_xact_lock`) that serialises every
  demotion, through the execution strategy per ADR 0014. The test asserts the *mechanism* rather
  than trying to win a race: it holds the same lock from another connection and proves the
  demotion waits. Removing the lock fails it, which was verified by doing exactly that.

- **Retention defaulted to 365 days, not the 30 the decision table recorded.** Flagged as an open
  decision when the plan was written and settled here rather than left implicit: `sign_in_events`
  is high-volume operational noise where a month is generous, while a privilege change is rare and
  is precisely the record wanted months later. It has its own knob
  (`Monitoring__AdminActionRetentionDays`), so 30 is one configuration value away — and the
  asymmetry runs one way, since deleted audit rows do not come back.

- **The audit is tracked, unlike `SignInAudit`, and the contrast is the design.** `SignInAudit`
  writes immediate SQL because it must record *failures*, which the unit of work discards along
  with the failed command. `AdminAudit` records only changes that happened, so it wants the
  opposite guarantee — the row and its effect commit together. One documented exception: a
  demotion lands immediately because its rail owns a transaction, so a failed commit can leave a
  demotion applied and unaudited. That is the safer direction, and it is argued on the port rather
  than hidden.

- **Integration tests had to opt into string enums explicitly.** The API serialises enum names
  (root `CLAUDE.md`, Notifications), but `ReadFromJsonAsync` with framework defaults expects
  integers and threw. Supplying a `JsonStringEnumConverter` in the test mirrors what the API
  actually sends, and incidentally makes these tests notice if that convention ever regresses.

- **`ChangeUserRole` reports success, not a conflict, when the role already matches.** The caller
  asked for a state the system is already in. It returns `false` and writes no audit row, so a
  retried click cannot pile up records claiming a role changed when it did not.

---

## Phase 3 — The screen

### Tasks

- [ ] **3.1** `/monitoring/users`: a paged, searchable table — username, display name, role,
      last seen, and a marker on accounts whose role comes from configuration.
- [ ] **3.2** Role change and force-sign-out as row actions, each behind a confirmation naming
      the user and the change in words ("Demote **ada** to Member?"). A misclick in a table row
      must not be able to demote anyone.
- [ ] **3.3** The configuration marker carries its own explanation on hover and in the confirm
      dialog: demoting this account will be undone at the next API restart unless the name is also
      removed from `Admin__Usernames`. This is the Phase 1 trap, surfaced at the one moment
      somebody is about to walk into it.
- [ ] **3.4** Per-user action history, from `GET /api/monitoring/users/{id}/actions`.
- [ ] **3.5** A link from the monitoring overview, beside jobs, sign-ins and traffic.
- [ ] **3.6** Component tests with MSW, including both refusals rendering as messages rather than
      as an empty table.
- [ ] **3.7** Playwright: an administrator promotes and demotes a second account; a member is
      refused the route. Both `@local-only` for the reason the existing monitoring specs are —
      only the stack `playwright.config.ts` starts names an administrator.

### Traps

- **The acting administrator's own row must not offer the actions their server will refuse.**
  Hide or disable them, *and* keep the server-side rules — the rails are the rule, the UI is the
  explanation.
- **A demotion does not sign the target out**, so nothing visible happens to them until their next
  request, at which point the nav entry disappears. That is correct and worth a line of copy, or
  it reads as a bug.

---

## Verification

Each phase ends green on `/verify`, plus CI's five jobs. Phase 2 touches a job handler, so the
worker's `codegen write` and the contract regeneration both apply — the two gates that fail on a
**diff** rather than a build error.

## Risks

- **Phase 1 grants standing access to anyone currently holding `Admin`.** Audit the table first.
  This is the only irreversible-by-omission step in the plan.
- **The two-step revocation is a documentation-carried rule**, not an enforced one. The
  `RoleIsConfigured` marker is the mitigation, and it is a mitigation rather than a fix. The
  alternative was "config only when there are zero admins", which removes the trap and the
  break-glass together.
- **`ReconcileAdministratorsHandler` is touched by Phase 1 and relied on by the registration path
  added in the monitoring PR.** They must keep agreeing. The directory's doc comment is the place
  that says why, and 1.1 must update it in the same commit.
