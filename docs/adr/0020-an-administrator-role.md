# 0020. An administrator role

**Date:** 2026-09-20
**Status:** Accepted

> **Written ahead of the implementation**, unlike ADR 0011, which was recorded from code that had
> already shipped. The decision is made and the reasoning below is final; the code lands in phase 1
> of `docs/superpowers/plans/2026-09-20-monitoring-page.md`. Where this ADR describes behaviour in
> the present tense it is describing what phase 1 must build, and a reviewer should treat any
> divergence as a bug in the implementation rather than a drift in the decision.

## Context

This application has never distinguished one user from another by privilege, and until now that
was not an omission. ADR 0007 put ownership **in the query**: `IOrderRepository.GetAsync` and
`ListAsync` both take an `ownerId`, and "there is no overload that can return an order the caller
does not own, so forgetting the filter on a future read is a compile error rather than a leak."
Every endpoint in the application returns either the caller's own data or the global catalogue.
`[Authorize]` with no policy — "any authenticated user" — has therefore been exactly the right
amount of authorization, and `[Authorize(Policy` appears nowhere in the solution.

The monitoring page breaks that arrangement, and breaks it at the root. Its entire purpose is to
read **across** owners: every job run regardless of who triggered it, every sign-in attempt
including ones against accounts that do not exist, traffic aggregated over the whole application.
ADR 0007's answer — make the repository incapable of returning someone else's row — is not
available here, because returning everyone's rows is the feature. Two of the page's endpoints are
worse than reads: retrying a dead-lettered job and triggering a scheduled job are writes whose
effects land on other people's work.

ADR 0017 saw this coming and named it. Its Context lists the roadmap this decision belongs to —
"admin authorization, run history, integration health checks, and the monitoring page itself are
separate pieces built later, on top of this one." This is that first piece.

ADR 0011 also anticipated it, in a way this ADR has to address directly. `User.SecurityStamp`'s
own summary lists what rotates the stamp: "a password change, a lockout, an explicit
sign-out-everywhere, and (from plan 2) a permission change." That parenthesis was written when no
permission model existed, and it assumed one shape for it. Part of this decision is that the
assumption does not hold.

## Decision

**`User` carries a `UserRole` — `Member` or `Admin` — and the role is read from the database on
every authenticated request, never carried in the cookie.**

### The role is a role, not a permission set

`enum UserRole { Member, Admin }` in `src/Domain/Users`, stored as a **string** column. Two
values, because there is one privileged page and no second axis of privilege to express. A grant
model — named permissions, roles composed of them, per-resource scoping — is a larger mechanism
built for requirements nobody has stated, and the exit is open: `Role` becomes one input to a
requirement handler rather than the whole of it, and the policy name at the call sites does not
change.

The enum is a string in the database for the reason the root `CLAUDE.md` already gives for
`NotificationKind` and `OrderStatus` crossing the wire as names: stored as an integer, adding a
member or reordering the ones that exist silently changes what every existing row means, with
nothing to catch it.

### The role is read per request, alongside the stamp

`SessionValidator` already issues "one projected, uncached read per authenticated request" — an
`AsNoTracking`, primary-key, single-column projection of `SecurityStamp`. **Adding `Role` to that
projection costs nothing**: same row, same index seek, same connection, one more column on the
wire. The port widens from `IsStampCurrentAsync` returning a `bool` to a validation returning both
the verdict and the role, and `OnValidatePrincipal` attaches the role to the principal for the
current request via `ReplacePrincipal`, without renewing the cookie.

This is the decision with the most consequences, and it is worth being explicit about what it
buys. **The cookie never carries the role, so the role can never be stale.** A demotion takes
effect on the demoted user's very next request. There is no window, no TTL, and no forced
sign-out. The authorization decision reads the same row, on the same request, that already decides
whether the session is valid at all.

It also means the read stays uncached, permanently, for a second independent reason on top of ADR
0011's. `HybridCache` is L1-only and cannot be evicted across pods (ADR 0010); a cached role would
let a revoked administrator keep administrative access on one replica for the length of the TTL.
`GetUser` must not become `ICacheable` — that rule now has two ADRs behind it.

### A role change does NOT rotate the security stamp

This is a deliberate departure from what ADR 0011's summary anticipated, and the anticipation
should be read as superseded rather than unimplemented.

Rotation exists to invalidate credentials that are **already issued and cannot otherwise be
reached** — a cookie minted under an old password, a session held by an intruder. The role is not
in the cookie, so there is no issued credential carrying stale authority to invalidate. Rotating
on a role change would sign the user out of a session they remain perfectly entitled to hold, to
fix staleness that the per-request read has already made impossible. Promotion would sign out the
person being promoted; a reconciler that rotated on every startup would sign out every
administrator on every API restart.

`User.SecurityStamp`'s summary needs its parenthetical corrected when phase 1 lands. The list of
rotation triggers stays at three.

### Administrators come from configuration, reconciled at startup

`Admin__Usernames` — a configuration array — is the authority for who is an administrator.
An `AdminReconciler` hosted service runs at API startup and issues one idempotent `UPDATE`:
promote every listed username that is not already `Admin`, demote every `Admin` that is not
listed. It matches on `UsernameNormalized`, never `Username`, because `User.Normalize` is the only
lookup key this application has (ADR 0006).

Configuration rather than a database-managed list, for one reason that dominates: **there is no
bootstrapping paradox.** A database-managed list needs an administrator to appoint the first
administrator, which means a seed, a CLI, or manual SQL — three mechanisms where configuration is
already a mechanism this repo has. It works identically on a fresh database with no users, in
`ApiFactory`, in the e2e stack, and in the kind overlay, and it is greppable in the way the root
`CLAUDE.md` says `JobRegistration` is greppable.

**Reconciling rather than seeding is the load-bearing half.** A seed only ever adds. Reconciling
means the configuration is *declarative*: removing a name from the list revokes the role at the
next startup, rather than leaving a former administrator privileged forever because nothing ever
took it away.

Both replicas run the reconciler. That is safe because the `UPDATE` is idempotent and conditional
on the role actually differing, so concurrent identical statements are harmless and a restart with
an unchanged list writes nothing at all.

**Amended 2026-09-20: registration reads the same list.** Reconciling at startup alone left one
gap, and the reconciler's own warning named it — *"`Admin:Usernames` names accounts that do not
exist. They hold no access until they register."* On a genuinely fresh deployment that is the
normal path, not an edge case: the operator is configured before they have an account, registers,
sees an ordinary member's shell, and stays one until somebody restarts the API. `RegisterUser`
therefore asks the same configured list, through an `IAdministratorDirectory` port over the same
`AdminOptions`, and registers the account with `Admin` when it is named.

This moves no authority. Configuration is still the only thing that grants the role, the match is
still `User.Normalize`, and the startup reconcile still overrides everything at the next boot — so
the two paths cannot disagree about a given list. It grants nothing a restart would not have
granted moments later, which is also why it opens no new squatting risk: a name in the list is
claimable by whoever registers it first, and that was already true.

The port is registered in `AddInfrastructure`, not in `AddAdministratorRoles`. Only the API calls
the latter, while `AddMessaging` registers `RegisterUserHandler` in *every* host and the generic
host validates every descriptor it can construct — putting it beside the reconciler fails the
worker's container build, the same way an unregistered `IClientContext` already did once.

### Refused with 403, and that does not contradict ADR 0006 or 0007

Monitoring endpoints answer a non-administrator with **403**, not 404.

This repo has a strong and correct habit of refusing to confirm existence: ADR 0006's uniform
sign-in error and always-hashed password, and ADR 0007's decision that "a cross-user id already
produces the same `order.not_found` failure as an id nobody ever issued." Both protect the
existence of a *resource* — this username, this order id — that an attacker would otherwise learn
by probing.

A monitoring route leaks nothing by admitting it exists. Its path is a fixed string compiled into
the SPA bundle that every user downloads. There is no id to probe and no set to enumerate. 403 is
the accurate answer — the route exists, you are authenticated, you may not — and returning 404
would only make a legitimate administrator debugging their own missing access believe the
deployment was broken.

### The nav entry is not the control

The SPA renders the monitoring link only for administrators, from the role now returned by
`GET /api/auth/me`. That is cosmetics. The authorization is the policy on the controller, and the
test suite asserts the server side independently of anything the client does.

## Consequences

**Every authenticated request now reads one more column.** Genuinely marginal — it is the same
row, already being fetched, on an index this query already seeks — but the honest framing is that
ADR 0011's per-request read has just acquired a second caller, which makes it harder to ever
remove. The exit ADR 0011 recorded (interval-based revalidation, in the manner of ASP.NET Core
Identity's `SecurityStampValidator`) now trades away *two* immediacy guarantees rather than one,
and whoever profiles this should know that revoking an administrator would become eventually
consistent along with revoking a session.

**`ISessionValidator`'s signature changes, and with it a published seam.** `IsStampCurrentAsync`
returning `bool` becomes a validation returning a verdict plus a role. Every call site and test
double moves. This is a small change made once, but it is a change to a type that ADR 0011
documents by name.

**Existing cookies keep working, and nobody is signed out by this deploy.** The role is not a
claim, so a cookie minted before this change is missing nothing — the role is supplied fresh on
every request from the database. This is a pointed contrast with ADR 0011's own deploy, which
retired every live session by design. Here the migration is additive, existing rows default to
`Member`, and the change is invisible to everyone who is not being promoted.

**Configuration is the authority, which makes manual promotion futile.** An administrator
appointed by hand-written SQL is demoted at the next API start. That is the declarative property
working correctly, but it is a sharp edge: the failure mode is "my change silently reverted on the
next deploy", which is exactly the kind of thing that costs an afternoon if it is not written
down. It is written down here and belongs in the root `CLAUDE.md` when phase 1 lands.

**Rotating an administrator out requires a deploy.** Configuration changes are deployment
artifacts. For a monitoring page this is acceptable — the population is small and changes rarely —
but it is the concrete cost of refusing an in-app user-management screen, and it is the trigger to
revisit that refusal.

**The reconciler must tolerate a database that is not ready.** It runs at startup, which can
precede migrations. It fails the reconcile and logs at `Warning`; it never fails the host. An API
that refuses to start because it could not confirm the administrator list is an API that turns a
configuration problem into an outage.

**A second authorization axis now exists, so every new endpoint has a question to answer** that it
did not have before. Previously `[Authorize]` was the only answer. This is the ordinary cost of
having privilege at all, and it is why the role stayed at two values.

**This rules out per-resource administration.** There is no "administrator of these users" or
"read-only operator". An `Admin` sees everything the monitoring page exposes and can perform both
of its actions. If a read-only operator role is ever wanted, that is the point at which the grant
model this ADR declined becomes the right answer.

## Alternatives considered

**Carry the role as a claim in the cookie, written at sign-in.** The obvious design, and the one
this plan originally specified. Rejected once it was noticed that `SessionValidator` already reads
the user row on every request, which makes the database read free and the claim strictly worse. A
cookie-borne role is stale by construction: a demotion does not take effect until the cookie is
re-issued, so the demotion has to force a sign-out by rotating the security stamp, which in turn
means every promotion signs the promoted user out, and a startup reconciler has to be carefully
conditional or it logs every administrator out on every restart. It also adds a second claim-name
landmine of the kind ADR 0011 describes — changing the constant's value silently strips authority
instead of granting it. Every one of those problems is an artifact of putting mutable authority in
an immutable token, and all of them vanish when the authority is read fresh.

**A config-only gate, with no role at all** — register the monitoring endpoints only when
`Monitoring__Enabled` is on. Rejected: it makes the deployment the security boundary, so the page
is either visible to *every* authenticated user or to nobody. In any environment where an operator
needs it, every ordinary user can read every other user's sign-in history. A feature flag is not
an authorization model.

**A permission or grant model** — named permissions, roles composed of them. Rejected as
speculative. There is one privileged surface and no second axis of privilege in evidence; the
smallest thing that expresses the requirement is an enum with two members, and the migration path
to a grant model does not require undoing this one.

**Per-user view only, with no privileged role** — the monitoring page shows each user their own
jobs and their own logins. Rejected because it is not an operations page. The questions it exists
to answer — is the outbox backing up, which jobs are dead-lettering, is anyone being brute-forced
— are all questions *across* users, and none of them can be asked from inside one user's slice.

**Seeding an administrator in a data migration**, promoting the earliest-registered account.
Rejected: it bakes an identity into migration history, which is then immutable by this repo's own
rule against hand-editing applied migrations, and it does nothing at all on a fresh database with
no users — which is every CI run and every new developer's first start.

**An in-app user-management screen**, bootstrapped some other way. Not rejected on the merits —
it is the right answer eventually, and it is what removes the deploy-to-revoke cost above. It lost
on scope: it is a second feature, with its own authorization questions and its own audit
requirements, and making the monitoring page wait for it would be the tail wagging the dog.
