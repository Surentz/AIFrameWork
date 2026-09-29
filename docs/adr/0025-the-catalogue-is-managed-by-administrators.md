# 0025. The catalogue is managed by administrators

**Date:** 2026-09-27
**Status:** Accepted

**Supersedes** ADR 0013's decision that the catalogue is "writable by any signed-in caller", and
only that. Everything else ADR 0013 decided — one global catalogue behind the caller-scoped
cache, and the staleness that comes with it — stands.

## Context

ADR 0013 made the catalogue writable by any signed-in caller "because there is no role system
yet". ADR 0020 added the `Admin` role, and ADR 0024 named the capability for this —
`Catalogue.Manage` — but deliberately did not apply it, because of what it costs the end-to-end
suite: nearly every e2e spec arranges a product, and after this change only an administrator can
create one. That was about 16 of the 27 tests a kind-cluster run executes, and only the stack
Playwright manages named an administrator (`e2e-admin`, via `Admin__Usernames`).

Any member being able to rename a product or change its price is the defect. A price change
notifies everyone who ever bought the product (the `ProductPriceChanged` fan-out), so today any
signed-in user can put a message in other people's feeds.

## Decision

**Creating and editing products requires `Catalogue.Manage`; reading the catalogue stays open to
every signed-in caller.**

- `ProductsController`'s `Create` and `Update` carry `[Authorize(Policy = Catalogue.Manage)]`
  **per action**. The controller keeps its plain `[Authorize]`, because the reads are the whole
  shop's. That is the opposite default from the admin-only controllers, where the policy sits on
  the controller so a new action is gated automatically — here a new *write* action has to carry
  the policy itself, and review has to catch one that does not.
- A member gets **403**, not 404: the route is in the SPA bundle every user downloads.
- The SPA hides "Add a product" and "Edit" from members, and routes `/products/new` and
  `/products/:id/edit` through `RequireRole`. That is an explanation, not the control.
- **The kind overlay names `e2e-admin` in `Admin__Usernames`**, so the cluster run keeps its order,
  catalogue and notification coverage rather than losing most of it to `@local-only`.

## Consequences

- A member can no longer change a price, and with it can no longer notify every past purchaser.
- **The kind overlay grants a role by configuration.** Whoever registers `e2e-admin` first on a
  cluster is its administrator. Acceptable on a throwaway localhost cluster that the e2e run
  itself seeds, and the reason `config.yaml` says in so many words never to copy the line into an
  overlay other people can reach.
- **The kind run's auth budget tightens, by one call.** The operator signs in once per run, in
  `e2e/setup/seed-admin.ts` (a Playwright `globalSetup` on every target), and saves the session
  for every worker to load, so workers never sign in as it. A run spends eight of the ten permits,
  and nine on a cluster that has never seen the account (a refused sign-in, then the
  registration). Recounted in `frontend/e2e/CLAUDE.md`.
- **Any other e2e target has to name `e2e-admin` too**, or nearly every spec fails with a 403.
  That includes `npm run e2e:url` against the dev loop, whose configuration names no
  administrator.
- ADR 0013's staleness is unchanged, as ADR 0013 predicted: "Restricting writes to
  administrators shrinks the number of people who can cause staleness; it does not change who
  sees it."
- The integration suite creates its products through an administrator (`CatalogueSetup`), which is
  one extra registration per product arranged. Cheap under the test host's raised rate limit.

## Alternatives considered

**Tag every product-creating e2e spec `@local-only`.** No configuration change at all. Rejected:
the kind run would drop to about ten tests, and the cluster run exists to exercise two replicas,
caching on and durable Wolverine against exactly the order, catalogue and notification flows that
would leave it.

**Keep the policy on the controller and exempt the reads with a second, member policy.** Gated by
default, which is the property the admin controllers are built around. Rejected: it inverts what
the catalogue is — a member-facing read with an operator-only write — and a read policy that every
member satisfies is `[Authorize]` spelled differently.

**Hide the forms in the SPA and leave the API open.** Rejected on sight: the SPA is not the
security boundary anywhere in this application (ADR 0020).
