# Sidebar navigation and a dashboard landing page

**Type:** `feat(frontend)`. Frontend only: no API, contract or migration change, so none of the
three committed generated artifacts moves.

## Intent

The top bar holds eight flat links and grows with every feature. Move navigation to a left
sidebar, group the links by purpose, and make a new dashboard the page a signed-in user lands on.
The dashboard shows one tile per group, with the group's pages as subtiles.

Success: a member sees only what they can use; finding a page is two clicks from anywhere; adding
a page is one entry in one file; no existing URL changes.

## Decisions (agreed in brainstorming)

- Tiles are **navigation only**: icon, title, one-line description, subtile links. No new queries.
- Sidebar groups are **collapsible**. Dashboard tiles always show every subtile.
- Groups, in order:

| Group | Items | Visible to |
|---|---|---|
| (top level) | Dashboard `/` | everyone |
| Sales | All orders `/orders`, Place an order `/orders/new`, Exports `/orders/exports` | everyone |
| Catalogue | Products `/products` | everyone |
| Insights | Population `/statistics/population` | everyone |
| Operations | Fulfilment `/fulfilment`, Monitoring `/monitoring` | `Admin` |
| Account | Password `/account/password` | everyone |

- The bell and the assistant icon stay in a slim top strip with the user name and Sign out. They
  are not groups and get no tile.
- The first visit starts **mostly folded**: only the group holding the current page is open.
- No expand-all / collapse-all control.
- Monitoring keeps its own hub at `/monitoring`; its five drill-downs are not separate tiles.

Mockups: `.superpowers/brainstorm/325-1791830762/content/` (`dashboard-layout.html`,
`collapsible-groups.html`; gitignored).

## Design

### `src/navigation.ts` (new): the single source

```ts
export interface NavItem { label: string; to: string; description: string; end?: boolean }
export interface NavGroup { id: string; label: string; description: string; icon: NavIcon;
                            adminOnly?: boolean; items: readonly NavItem[] }
export const navGroups: readonly NavGroup[];
export function visibleGroups(role: string | undefined): readonly NavGroup[];
export function groupForPath(pathname: string): NavGroup | undefined;
```

- `visibleGroups` drops admin groups for non-admins. The admin filter lives here and nowhere
  else. As today it is cosmetics, not the control: the API's capability policies and
  `RequireRole` in `routes.tsx` still refuse (ADR 0020).
- `end` carries today's `NavLink end` behaviour (`/orders`, `/products`) so `/orders/new` and
  `/products/:id` do not light up the list entry.
- `groupForPath` finds the group whose item best matches the path (longest matching `to`), so
  `/orders/:id` opens Sales and `/monitoring/jobs` opens Operations.
- Icons are small inline SVG components in one `NavIcon` file. No icon dependency is added.

### Shell (`AppLayout.tsx`, `AppLayout.css`)

- CSS grid: fixed-width sidebar column and a content column. The brand moves to the sidebar and
  links to `/`.
- New `Sidebar` component: `<nav aria-label="Main">`, a Dashboard link, then one section per
  visible group. The label is `Main` because the assistant's conversation list is already
  `navigation` named `Conversations` and both exist on `/assistant`.
- Group headings are `<button aria-expanded aria-controls>`; Enter and Space toggle. A heading
  whose group holds the current page and is folded shows a dot.
- Fold state is a `useNavFold` hook: `Record<groupId, boolean>` in `localStorage`, every read and
  write in try/catch, rendering correctly with storage unavailable. On every navigation the group
  from `groupForPath` is forced open; others keep their stored state, defaulting to closed.
- Below 48rem the sidebar is an overlay opened by a menu button in the top strip
  (`aria-expanded`), closed by navigation, Esc, or the scrim. Focus returns to the button.
- The top strip keeps `NotificationBell`, `AssistantLink`, user name, Sign out and
  `useNotificationStream()` exactly as now. The logout `ErrorPanel` stays above the content.
- Assistant full-bleed mode survives: the content column fills the viewport height there, and the
  assistant keeps its own conversation sidebar beside the new one. This is the one place two
  sidebars coexist; the plan must check it at widths down to the breakpoint.

### Dashboard (`features/dashboard/DashboardPage.tsx`, new)

- Route `/` renders the page; `<Navigate to="/orders">` goes. Login still lands on `/`.
- Greeting from `useSession().displayName`, then a grid of tiles from `visibleGroups(role)`. A
  tile is a labelled region: icon, heading, description, and a list of subtile links, each with
  its item description as supporting text.
- The grid is one column on narrow screens, two from 40rem, three from 64rem.
- Error and loading: the only data is the session, which `RequireAuth` has already resolved, so
  there is no query and no error branch to add. If `session` is momentarily undefined the page
  renders the tiles for a non-admin and corrects itself, as the current nav does.

### Things that break silently, and the answer to each

1. **Duplicate link names.** The sidebar and the dashboard both render links called "Monitoring",
   "Exports" and so on. Playwright's `getByRole('link', { name })` is strict, so `/` would throw
   on every one. Dashboard subtile links get a more specific accessible name via `aria-label`
   (for example "Exports, in Sales"), and the e2e `navLink` helpers
   (`monitoring.ts`, `fulfilment.ts`, `exports.ts`) are scoped to `navigation 'Main'`.
2. **Folded groups hide their links.** A folded section is `hidden`, so a helper clicking
   "Monitoring" on `/orders` would fail. The `navLink` helpers open the group heading first when
   the link is not visible; one helper, `openNav(page, label)`, in `e2e/screens/`.
3. **Landing page changes.** Anything that assumes sign-in or `/` ends on `/orders` (a
   post-login URL assertion in a Vitest or Playwright screen, or a `renderAt('/')`) is updated to
   expect the dashboard. Tests that render `/orders` directly are unaffected. The plan starts by
   grepping for these.

## Testing

- `navigation.test.ts`: `visibleGroups` for member and admin; a group left empty is dropped;
  `groupForPath` for `/orders`, `/orders/new`, `/orders/42`, `/products/7/edit`,
  `/monitoring/jobs`, an unknown path; no two items share a `to`.
- `Sidebar.test.tsx`: member sees no Operations; admin does; the active link is marked
  (`aria-current`); the current group opens on navigation; clicking a heading toggles it and
  flips `aria-expanded`; fold state persists across a remount; works when `localStorage` throws;
  mobile overlay opens, closes on navigation and Esc.
- `DashboardPage.test.tsx`: tile per visible group, subtile links with correct hrefs, admin tile
  absent for a member, greeting.
- `routes.test.tsx`: `/` renders the dashboard.
- Playwright: update the three `navLink` helpers and any post-login assertion; one new spec
  (`specs/navigation/dashboard.spec.ts`): sign in, land on the dashboard, open Sales via its
  tile, reach All orders; an admin reaches Monitoring through the sidebar; a member has no
  Operations tile or heading.
- jsdom has no layout, so the mobile overlay's visibility is asserted by state and attributes in
  Vitest and by viewport size in Playwright.

## Documentation

- ADR `0035-grouped-sidebar-navigation-with-a-dashboard-landing-page.md` via `/adr`: why one
  config feeds both views, why folding is per-browser, why cosmetics-only role filtering.
- `frontend/CLAUDE.md`: a new page is added to `navigation.ts`, or it is reachable only by URL.
- `frontend/e2e/CLAUDE.md`: the `openNav` rule.

## Out of scope

Live figures on tiles; a collapsible desktop sidebar (icon rail); favourites or recents;
Monitoring drill-downs as tiles; sidebar entries for Assistant and Notifications; any backend or
contract change.
