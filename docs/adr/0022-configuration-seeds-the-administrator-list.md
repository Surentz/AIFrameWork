# 0022. Configuration seeds the administrator list, rather than mirroring it

**Date:** 2026-09-21
**Status:** Accepted

**Supersedes** ADR 0020's *"Administrators come from configuration, reconciled at startup"*
section, and only that section. Everything else ADR 0020 decided — the role is a role rather than
a permission set, it is read per request rather than carried in the cookie, a role change does not
rotate the security stamp, refusal is 403 — is unchanged and still binding.

> The code lands in phase 1 of `docs/superpowers/plans/2026-09-21-user-management.md`, which is
> this ADR and nothing else. Phases 2 and 3 build the screen this unblocks.

## Context

ADR 0020 made `Admin__Usernames` the whole truth about who administers this application.
`AdminReconciler` runs at every API start and makes the stored roles *match* the configured list
exactly: promote everyone named, **demote every administrator who is not**. That second half was
deliberate and load-bearing — it is what made revocation work by removing a name, and ADR 0020
called it "the load-bearing half" for good reason. A seed only ever adds; reconciling made
configuration declarative.

It also recorded the cost, and named the trigger for revisiting it:

> **Rotating an administrator out requires a deploy.** Configuration changes are deployment
> artifacts. For a monitoring page this is acceptable — the population is small and changes rarely
> — but it is the concrete cost of refusing an in-app user-management screen, and it is the
> trigger to revisit that refusal.

That trigger has been hit. The refusal is being revisited, and a user-management screen is being
built.

**The two cannot coexist.** A screen that grants the role writes to the database; a reconciler
that mirrors configuration overwrites the database at the next start. Every grant made in the UI
would be silently undone by the next deploy — no error, no failed request, and nothing in the logs
naming the cause. The person who granted it would see it work, and someone would discover weeks
later that it had not.

So one of the two has to give, and the question is which.

## Decision

**The reconciler promotes and never demotes. Configuration becomes a floor, not a mirror.**

`ReconcileAdministrators` is handed the configured list and promotes every named account that does
not already hold the role. An administrator the list does not name is left exactly as they are,
because something else — the screen — may have granted it deliberately, and this process cannot
tell a deliberate grant from a stale one.

Three consequences follow immediately, and all three are the point:

**The database becomes the authority for revocation.** Removing a name from `Admin__Usernames`
revokes nothing. Revocation is an in-app action, recorded and attributable, which is strictly
better than a configuration diff in a deploy — an audit row names who did it and when, and a
config change names neither.

**Configuration becomes a permanent break-glass path.** Keeping your own username listed means you
can always get back in, whatever anyone does in the UI, by restarting the API. That is the whole
reason for keeping the mechanism rather than deleting it once the screen exists. A system whose
only administrators can lock themselves out with one click, and whose only recovery is hand-written
SQL, is a worse system.

**It still bootstraps an empty database.** Nothing changes about the property ADR 0020 valued
most: there is no paradox where an administrator is needed to appoint the first administrator. A
fresh database, `ApiFactory`, the e2e stack and the kind overlay all still work identically.

Idempotence is preserved. `User.ChangeRole` reports whether it actually moved, so a run against a
list whose members already hold the role dirties no entity and issues no UPDATE — which is what
keeps this safe to run in both replicas at every start.

### The sharp edge, stated plainly

**An account demoted in the application while still named in `Admin__Usernames` is promoted
straight back at the next API start.**

It will look like the demotion did not save. It did; configuration re-granted it afterwards.

> **Removing an administrator takes both steps: demote them in the application, and remove their
> name from `Admin__Usernames`.** The app alone is undone at the next restart; configuration alone
> now does nothing at all.

This is the price of the break-glass path, and it was chosen with the price visible. Two things
mitigate it, neither of which is a fix:

- The reconciler logs **one structured event per promotion, naming the user** — `{Username}` as a
  filterable property rather than a joined blob — so "why is this person an administrator again"
  is answerable from the log store rather than by reading source.
- The users screen (phase 2) marks every account whose role is currently backed by configuration,
  so the conflict is visible *before* someone demotes them rather than after a restart undoes it.

### What did not change

`RegisterUser` still consults the same list through `IAdministratorDirectory`, promoting a
configured name at the moment it registers. Under the old mirror that was a convenience — the next
restart would have done it anyway. Under a floor it is the same convenience for the same reason,
and the two paths still cannot disagree, because both now only ever promote.

## Consequences

**Any account currently holding `Admin` keeps it permanently, until someone acts.** This is the
one irreversible-by-omission effect of this change, and the only one that needs doing *before*
deploying rather than after. Under the old behavior an unintended administrator was self-correcting
— the next restart demoted them. Under this one, nothing does. **Audit the `users` table for
`Admin` rows before this ships.** Until phase 2 exists there is no in-app way to demote anyone, so
between this ADR and that screen the only correction is SQL — and unlike before, SQL now *sticks*.

**`AdministratorReconciliation` changes shape, and it is a published seam.** `Demoted` is gone and
`Promoted` becomes the usernames rather than a count, because a count cannot answer the question a
floor invites. Its only consumer is `AdminReconciler`, so the blast radius is one file plus its
tests.

**The reconciliation query narrows.** `ListForRoleReconciliationAsync` no longer loads current
administrators absent from the list — under promote-only they could produce nothing but a no-op.
The read is now bounded by the configured list alone. Slightly cheaper, and more to the point, the
narrowing *is* the mechanism: while that query returned them, the handler had something to demote.

**ADR 0020's own summary is now wrong in one place**, and it is left standing with this ADR
superseding it rather than edited. Its "Configuration is the authority, which makes manual
promotion futile" consequence inverts: manual promotion, by SQL or by the coming screen, now
persists.

**Nobody is signed out, and no migration is needed.** The role is not a claim and this changes no
schema. The change is invisible to every user who is not being promoted, and invisible even to them
beyond gaining access.

**A test inverts rather than being deleted.** `ReconcileAdministratorsHandlerTests`'s demotion case
becomes `An_administrator_absent_from_configuration_is_left_alone`. That test is where this
decision is encoded executably, which is why it keeps a name describing the rule rather than the
method.

## Alternatives considered

**Consult configuration only when there are zero administrators.** A true bootstrap: the list
seeds an empty system, and once any administrator exists configuration is ignored entirely. This is
the cleanest mental model by some distance — the database is the single authority, full stop, and
the two-step revocation trap above does not exist at all. Rejected for one reason: it deletes the
break-glass path. Lock every administrator out — one careless demotion, or one person leaving —
and the only recovery is hand-written SQL against production. The trap is a documented, visible
cost paid by people doing routine work; no recovery path is an undocumented cost paid by someone
at the worst possible moment. Worth revisiting if the marker in phase 2 proves insufficient and
people keep walking into the re-promotion.

**Keep configuration authoritative and let the screen manage everything else** — lockouts,
sessions, deactivation — with the role staying a deploy. Rejected: it leaves the actual problem
untouched. The thing that costs a deploy is promoting and demoting administrators, and a
user-management screen that cannot manage the one privileged attribute is not the feature that was
asked for.

**Make the reconciler demote only accounts it previously promoted**, tracking provenance on the
user row — a `RoleGrantedBy` column distinguishing a configured grant from an in-app one. This
genuinely resolves the conflict: configuration stays declarative over its own grants, and in-app
grants are untouched. Rejected as too clever for the problem. It adds a column, a migration and a
concept to every future conversation about roles, in exchange for removing one documented
two-step procedure — and it has its own confusing edge, where the same person granted both ways
has a provenance that depends on ordering.

**Remove the configuration path entirely once the screen exists**, seeding the first administrator
with a one-off CLI command or a startup flag. Rejected on the same grounds as the zero-admins
option, plus one more: a one-off command is a mechanism that exists solely to be used once, which
means it is never exercised and is broken the day it is needed.
