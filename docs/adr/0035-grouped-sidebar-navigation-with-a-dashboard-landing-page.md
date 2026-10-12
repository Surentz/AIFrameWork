# 0035. Grouped sidebar navigation with a dashboard landing page

**Date:** 2026-10-12
**Status:** Accepted

## Context

The shell was a top bar of eight flat links, two of them administrator-only, and every new feature
added another. Signing in landed on the orders list, so there was no place that showed what the
application offers. The request was a left sidebar, links grouped by purpose so the menu does not
grow without end, and a dashboard as the starting page with a tile per group and a subtile per page.

Constraints that were real:

- Role gating is cosmetic in the frontend. The API's capability policies and `RequireRole` are the
  control (ADR 0020); hiding a link must not be mistaken for protecting a page.
- The e2e suite finds elements by role and accessible name, strictly. The catalogue screen locates
  `heading "Catalogue"`, the assistant page already has a `navigation` named "Conversations", and
  Playwright fails a locator that matches two elements.
- Sign-in and registration hard-coded `navigate('/orders')`, and the root route redirected to
  `/orders`, so "the starting page" had three places to change.
- The change is frontend-only. No API, contract or migration moves, so none of the committed
  generated artifacts change.

Design: `docs/superpowers/specs/2026-10-12-sidebar-navigation-and-dashboard-design.md`.

## Decision

- **One config feeds both views.** `src/navigation.ts` declares the groups (Sales, Catalogue,
  Insights, Operations, Account) and their pages. The sidebar and the dashboard both render from it.
  `visibleGroups(role)` drops administrator groups and is the only place that filter exists. A page
  that is not in the config is reachable only by URL.
- **The sidebar groups fold.** Group headings are `<button aria-expanded>`s, closed by default. The
  group holding the current page opens on load and whenever navigation moves into it, even if the
  user had folded it; staying on a page inside a group the user folds leaves it folded. The open set
  is remembered per browser in `localStorage`, and every access is guarded so the UI works without it.
- **The dashboard is navigation only.** `/` renders a tile per visible group with a subtile per page.
  It makes no request of its own, so it has no loading or error state. Sign-in and registration now
  land on `/`.
- **Names are chosen to keep role-based locators unambiguous.** The sidebar's navigation is labelled
  "Main". Sidebar group headings are buttons, never headings. A dashboard subtile's accessible name
  is `"<page>, in <group>"`, so it never matches the sidebar's link to the same page.
- **Small screens get an overlay.** Below 48rem the sidebar is a fixed panel opened by a menu button
  in the top strip. It closes on navigation, on Escape (returning focus to the button) and from a
  scrim. The bell, the assistant icon, the user's name and Sign out stay in a slim top strip, so they
  are on every screen.
- **The catalogue's link is now called "Products".** "Catalogue" is the group.

## Consequences

Easy:

- Adding a page is one entry in `navigation.ts`, and the sidebar and dashboard cannot disagree.
- A member never sees an administrator link, in the DOM or hidden, and an administrator's extra group
  appears when the session resolves without a reload.

Harder, and the costs:

- A page's sidebar link is not visible while its group is folded, so e2e specs must reach a page
  through `shell.goTo`, which unfolds the group first. A helper that clicks a link directly now fails
  on a folded group.
- Fold state lives in one browser. It does not follow the user to another device and is lost when
  site data is cleared. It is a convenience, not a preference stored on the server.
- The assistant page shows two navigations side by side: the sidebar and its own conversation list.
  Its full-height layout has to be checked at narrow widths.
- The dashboard shows no live figures. A tile cannot say "3 orders awaiting shipment" until a later
  change adds queries, and probably an API endpoint, for it.
- Existing tests that assumed the post-sign-in page was the orders list had to change, and the
  catalogue link's name changed in the unit tests that look for it.

Ruled out: an icon-rail (collapsed) desktop sidebar, favourites or recents, and an expand-all control.

## Alternatives considered

- **Hand-written navigation in the sidebar and again on the dashboard.** The smallest change, but the
  two lists drift apart as pages are added, which is the problem the request was about.
- **Derive navigation from the route table.** Routes carry no label, group, description or role, so
  metadata would have to be added to them anyway, and parameterised routes such as `/orders/:id`
  would need excluding. A separate config is simpler and explicit.
- **Dashboard tiles with live counts, or counts on only a few tiles.** Rejected for this change: it
  needs new queries and probably a summary endpoint, which would move the API contract. Tiles were
  kept navigation-only; live figures can be added per tile later.
- **Group headings as real `<h2>` elements.** Rejected because the e2e catalogue screen finds
  `heading "Catalogue"` and a sidebar heading of the same name would make that locator ambiguous.
- **All groups open on first visit.** Rejected for a sidebar that stays short: only the group holding
  the current page starts open.
- **Sidebar entries for the assistant and notifications.** Left as header icons beside the bell, where
  they are on every screen, rather than duplicated as a group or a tile.
