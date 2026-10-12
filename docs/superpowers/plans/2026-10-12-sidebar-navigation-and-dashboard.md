# Sidebar navigation and dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the top navigation bar with a grouped, collapsible left sidebar, and add a dashboard (tiles per group, subtiles per page) as the page a signed-in user lands on.

**Architecture:** One typed config (`src/navigation.ts`) declares the groups and pages; the sidebar and the dashboard both render from it, with the administrator filter applied in one place. Fold state lives in a small hook backed by `localStorage`. The shell (`AppLayout`) becomes a sidebar column plus a content column; below 48rem the sidebar is an overlay behind a menu button. Frontend only: no API, contract or migration change.

**Tech Stack:** React 19, react-router-dom, TypeScript 6 (strict), Vitest + React Testing Library + MSW, Playwright, plain CSS with the tokens in `src/styles/tokens.css`.

**Spec:** `docs/superpowers/specs/2026-10-12-sidebar-navigation-and-dashboard-design.md`

## Global Constraints

All paths below are relative to `frontend/` unless they start with `docs/`. Run commands from `frontend/`.

- PR type is `feat`; PR title `feat(frontend): grouped sidebar navigation and a dashboard landing page`. Every commit subject is `feat(frontend): …` (or `docs(...)` for the ADR commit); every commit message ends with the trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Branch: `claude/sidebar-navigation-dashboard` (already created, spec already committed). Open the PR against `main` only.
- Function components only; explicit prop interfaces; explicit return types on exported functions.
- No `any`, no `!`. Lint runs `eslint . --max-warnings 0`: one warning is a failure.
- Never `catch {}` that is empty; a `catch` block must hold a statement or a comment saying why.
- `tsconfig` strictness: `strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`, `noImplicitReturns`. Never relax a flag.
- Every `localStorage` read and write is inside try/catch, and the UI renders correctly without it.
- Query keys, TanStack Query and error-state rules are unaffected: this change adds no query.
- Role filtering is cosmetics (ADR 0020): `RequireRole` and the API's policies remain the control. Do not touch `routes.tsx` gating.
- Playwright: specs import `test`/`expect` from `../../fixtures/index.ts` only; queries are role/label based, never `data-testid`; assertions never live in `screens/*.ts`; no new rate-limited auth call (the suite's budget is eight of ten permits).
- Use design tokens (`var(--color-…)`, `var(--space-…)`), never raw colours or pixel spacings. Dark mode comes free from the tokens.
- Breakpoint: 48rem. Below it the sidebar is an overlay.
- No heading elements in the sidebar: the e2e products screen locates `heading "Catalogue"`, so a sidebar heading of the same name would break strict mode. Group headings are `<button>`s.

## Review Focus

Failure modes the spec implies that are most likely to bite, most likely first. Each has a test in the task named in brackets.

1. **Deep link into a folded group.** Opening `/monitoring/jobs` (or reloading `/orders/exports`) with stored state that has the group folded must show it open with the page's link marked current; and `/orders/exports` must not mark "All orders" current. [Task 3: "opens the group of the current page", "marks only the exact page"; Task 2: "opens the group of a page navigated to"]
2. **Storage that is missing, refusing or hostile.** `localStorage` throwing, or holding `{not json`, `[1,2]`, `{"sales":"yes"}` or `null`, must degrade to "everything closed except the current group" with no exception. [Task 2: "survives unreadable or hostile stored state", "works with storage that refuses"; Task 3]
3. **A member sees nothing administrative.** No Operations button, no Fulfilment or Monitoring link in the DOM (not merely hidden), no Operations tile, whether the member arrives by the dashboard or types the address. [Task 1, Task 3, Task 5, Task 6]
4. **Session not yet resolved.** The shell and dashboard render before `/api/auth/me` answers; they must show the member view, then add Operations when an administrator's session lands, without a reload. [Task 5: "adds the operations tile once an administrator's session arrives"]
5. **Small-screen menu.** Opening, closing by scrim, Esc (focus returns to the menu button) and by following a link; and `/assistant`, the one page with two sidebars, keeps filling the viewport. [Task 4; the `/assistant` width check is the manual step in Task 8]

## File Structure

| File | Responsibility |
|---|---|
| `src/navigation.ts` (new) | The groups and pages as data; `visibleGroups`, `groupForPath`. No React. |
| `src/NavIcon.tsx` (new) | The five group icons as inline SVG. |
| `src/useNavFold.ts` (new) | Which groups are open; persistence; opening the current group on navigation. |
| `src/Sidebar.tsx`, `src/Sidebar.css` (new) | The sidebar: brand, Dashboard link, collapsible groups. |
| `src/AppLayout.tsx`, `src/AppLayout.css` (modify) | Shell grid, slim top strip, small-screen menu. |
| `src/features/dashboard/DashboardPage.tsx`, `dashboard.css` (new) | The tiles. |
| `src/routes.tsx` (modify) | `/` renders the dashboard. |
| `src/features/auth/LoginPage.tsx`, `RegisterPage.tsx` (modify) | Land on `/`. |
| `e2e/screens/shell.ts`, `dashboard.ts`; `monitoring.ts`, `fulfilment.ts`, `exports.ts` (modify/new) | Sidebar-aware locators. |
| `docs/adr/0035-…md`, `CLAUDE.md` files | Decision record and rules. |

---

### Task 1: The navigation config

**Files:**
- Create: `src/navigation.ts`
- Test: `src/navigation.test.ts`

**Interfaces:**
- Consumes: `UserRole` from `src/features/auth/types.ts` (`'Admin' | 'Member'`).
- Produces: `NavIconName`, `NavItem`, `NavGroup`, `navGroups`, `visibleGroups(role: UserRole | undefined): readonly NavGroup[]`, `groupForPath(pathname: string): NavGroup | undefined`. Group ids: `sales`, `catalogue`, `insights`, `operations`, `account`.

- [ ] **Step 1: Write the failing test**

Create `src/navigation.test.ts`:

```ts
import { groupForPath, navGroups, visibleGroups } from './navigation';

const memberIds = ['sales', 'catalogue', 'insights', 'account'];
const adminIds = ['sales', 'catalogue', 'insights', 'operations', 'account'];

describe('navigation', () => {
  it('shows a member every group except the administrators', () => {
    expect(visibleGroups('Member').map((group) => group.id)).toEqual(memberIds);
  });

  it('shows an administrator every group', () => {
    expect(visibleGroups('Admin').map((group) => group.id)).toEqual(adminIds);
  });

  it('treats a session that has not loaded as a member', () => {
    expect(visibleGroups(undefined).map((group) => group.id)).toEqual(memberIds);
  });

  it('gives every group at least one page', () => {
    for (const group of navGroups) {
      expect(group.items.length, group.id).toBeGreaterThan(0);
    }
  });

  it('never lists the same address twice', () => {
    const addresses = navGroups.flatMap((group) => group.items.map((item) => item.to));
    expect(new Set(addresses).size).toBe(addresses.length);
  });

  it.each([
    ['/orders', 'sales'],
    ['/orders/new', 'sales'],
    ['/orders/exports', 'sales'],
    ['/orders/42', 'sales'],
    ['/products', 'catalogue'],
    ['/products/new', 'catalogue'],
    ['/products/7/edit', 'catalogue'],
    ['/statistics/population', 'insights'],
    ['/fulfilment', 'operations'],
    ['/monitoring', 'operations'],
    ['/monitoring/jobs', 'operations'],
    ['/account/password', 'account'],
  ])('puts %s in the %s group', (path, id) => {
    expect(groupForPath(path)?.id).toBe(id);
  });

  it.each(['/', '/notifications', '/assistant', '/assistant/abc', '/nowhere', '/ordersx'])(
    'puts %s in no group',
    (path) => {
      expect(groupForPath(path)).toBeUndefined();
    },
  );
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `npx vitest run src/navigation.test.ts`
Expected: FAIL, `Failed to resolve import "./navigation"`.

- [ ] **Step 3: Write the implementation**

Create `src/navigation.ts`:

```ts
import type { UserRole } from './features/auth/types';

export type NavIconName = 'sales' | 'catalogue' | 'insights' | 'operations' | 'account';

export interface NavItem {
  readonly label: string;
  readonly to: string;
  /** Shown on the dashboard beside the page's link. */
  readonly description: string;
  /**
   * Light the link only on this exact path. /orders and /products need it, so that /orders/new and
   * /products/:id do not also light up "All orders" and "Products".
   */
  readonly end?: boolean;
}

export interface NavGroup {
  readonly id: string;
  readonly label: string;
  readonly description: string;
  readonly icon: NavIconName;
  /**
   * Cosmetics, not the control: the API refuses a non-administrator with a 403 whether or not the
   * group renders, and RequireRole explains the refusal on the page. Hiding it keeps a page nobody
   * can open out of everyone's navigation. See ADR 0020.
   */
  readonly adminOnly?: boolean;
  readonly items: readonly NavItem[];
}

/** The single source for the sidebar and the dashboard. A page that is not here is reachable only by URL. */
export const navGroups: readonly NavGroup[] = [
  {
    id: 'sales',
    label: 'Sales',
    description: 'Orders, placing them and exporting them',
    icon: 'sales',
    items: [
      {
        label: 'All orders',
        to: '/orders',
        end: true,
        description: 'Every order you have placed',
      },
      { label: 'Place an order', to: '/orders/new', description: 'Order something from the catalogue' },
      { label: 'Exports', to: '/orders/exports', description: 'Download your orders as a PDF' },
    ],
  },
  {
    id: 'catalogue',
    label: 'Catalogue',
    description: 'The product catalogue',
    icon: 'catalogue',
    items: [
      { label: 'Products', to: '/products', end: true, description: 'Browse the catalogue' },
    ],
  },
  {
    id: 'insights',
    label: 'Insights',
    description: 'Statistics and reports',
    icon: 'insights',
    items: [
      {
        label: 'Population',
        to: '/statistics/population',
        description: 'Population figures from Statistics Denmark',
      },
    ],
  },
  {
    id: 'operations',
    label: 'Operations',
    description: 'Administrators only',
    icon: 'operations',
    adminOnly: true,
    items: [
      { label: 'Fulfilment', to: '/fulfilment', description: 'Ship orders from the queue' },
      {
        label: 'Monitoring',
        to: '/monitoring',
        description: 'Jobs, sign-ins, traffic and integrations',
      },
    ],
  },
  {
    id: 'account',
    label: 'Account',
    description: 'Your sign-in',
    icon: 'account',
    items: [
      { label: 'Password', to: '/account/password', description: 'Change your password' },
    ],
  },
];

/** Until the session loads `role` is undefined: show the member view, then add to it. */
export function visibleGroups(role: UserRole | undefined): readonly NavGroup[] {
  return navGroups.filter((group) => group.adminOnly !== true || role === 'Admin');
}

/**
 * The group a path belongs to: the one whose page address is the longest prefix of it, so
 * /orders/42 is Sales and /monitoring/jobs is Operations. `startsWith(to + '/')` rather than
 * `startsWith(to)`, so /ordersx belongs to nothing.
 */
export function groupForPath(pathname: string): NavGroup | undefined {
  let best: { readonly group: NavGroup; readonly length: number } | undefined;
  for (const group of navGroups) {
    for (const item of group.items) {
      const matches = pathname === item.to || pathname.startsWith(`${item.to}/`);
      if (matches && (best === undefined || item.to.length > best.length)) {
        best = { group, length: item.to.length };
      }
    }
  }
  return best?.group;
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `npx vitest run src/navigation.test.ts`
Expected: PASS, all cases.

- [ ] **Step 5: Lint, format and commit**

```bash
npx prettier --write src/navigation.ts src/navigation.test.ts
npx eslint src/navigation.ts src/navigation.test.ts --max-warnings 0
npx tsc -b
git add src/navigation.ts src/navigation.test.ts
git commit -m "feat(frontend): describe the navigation groups as data

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Icons and the fold-state hook

**Files:**
- Create: `src/NavIcon.tsx`, `src/NavIcon.test.tsx`, `src/useNavFold.ts`, `src/useNavFold.test.ts`

**Interfaces:**
- Consumes: `NavIconName` from `src/navigation.ts`.
- Produces: `NavIcon({ name, size? })`; `useNavFold(activeGroupId: string | undefined): { isOpen(groupId: string): boolean; toggle(groupId: string): void }`. Storage key `aiframework.nav.open`.

- [ ] **Step 1: Write the failing tests**

Create `src/useNavFold.test.ts`:

```ts
import { act, renderHook } from '@testing-library/react';
import { useNavFold } from './useNavFold';

const storageKey = 'aiframework.nav.open';

beforeEach(() => {
  window.localStorage.clear();
});
afterEach(() => {
  vi.restoreAllMocks();
});

describe('useNavFold', () => {
  it('starts with only the group holding the current page open', () => {
    const { result } = renderHook(() => useNavFold('sales'));

    expect(result.current.isOpen('sales')).toBe(true);
    expect(result.current.isOpen('catalogue')).toBe(false);
  });

  it('starts with every group closed on a page that belongs to none', () => {
    const { result } = renderHook(() => useNavFold(undefined));

    expect(result.current.isOpen('sales')).toBe(false);
  });

  it('folds and unfolds a group', () => {
    const { result } = renderHook(() => useNavFold('sales'));

    act(() => {
      result.current.toggle('sales');
    });
    expect(result.current.isOpen('sales')).toBe(false);

    act(() => {
      result.current.toggle('sales');
    });
    expect(result.current.isOpen('sales')).toBe(true);
  });

  it('remembers what was opened across mounts', () => {
    const first = renderHook(() => useNavFold(undefined));
    act(() => {
      first.result.current.toggle('catalogue');
    });
    first.unmount();

    const second = renderHook(() => useNavFold(undefined));

    expect(second.result.current.isOpen('catalogue')).toBe(true);
  });

  it('opens the group of a page navigated to, even if it was folded before', () => {
    const { result, rerender } = renderHook(({ id }) => useNavFold(id), {
      initialProps: { id: 'sales' as string | undefined },
    });
    act(() => {
      result.current.toggle('sales');
    });
    expect(result.current.isOpen('sales')).toBe(false);

    rerender({ id: 'catalogue' });
    expect(result.current.isOpen('catalogue')).toBe(true);
    expect(result.current.isOpen('sales')).toBe(false);

    rerender({ id: 'sales' });
    expect(result.current.isOpen('sales')).toBe(true);
  });

  it('leaves a group the user folded alone while they stay on a page inside it', () => {
    const { result, rerender } = renderHook(({ id }) => useNavFold(id), {
      initialProps: { id: 'sales' as string | undefined },
    });
    act(() => {
      result.current.toggle('sales');
    });

    rerender({ id: 'sales' });

    expect(result.current.isOpen('sales')).toBe(false);
  });

  it.each(['{not json', '[1,2]', '{"sales":"yes"}', 'null', '"text"'])(
    'survives unreadable or hostile stored state: %s',
    (raw) => {
      window.localStorage.setItem(storageKey, raw);

      const { result } = renderHook(() => useNavFold('account'));

      expect(result.current.isOpen('account')).toBe(true);
      expect(result.current.isOpen('sales')).toBe(false);
    },
  );

  it('works with storage that refuses every read and write', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });

    const { result } = renderHook(() => useNavFold('sales'));
    expect(result.current.isOpen('sales')).toBe(true);

    act(() => {
      result.current.toggle('catalogue');
    });
    expect(result.current.isOpen('catalogue')).toBe(true);
  });
});
```

Create `src/NavIcon.test.tsx`:

```tsx
import { render } from '@testing-library/react';
import { NavIcon } from './NavIcon';
import { navGroups } from './navigation';

describe('NavIcon', () => {
  it.each(navGroups.map((group) => group.icon))('draws the %s icon, hidden from assistive tech', (name) => {
    const { container } = render(<NavIcon name={name} />);

    const svg = container.querySelector('svg');
    expect(svg).toHaveAttribute('aria-hidden', 'true');
    expect(svg?.children.length).toBeGreaterThan(0);
  });
});
```

- [ ] **Step 2: Run them to verify they fail**

Run: `npx vitest run src/useNavFold.test.ts src/NavIcon.test.tsx`
Expected: FAIL, cannot resolve `./useNavFold` and `./NavIcon`.

- [ ] **Step 3: Write the implementations**

Create `src/NavIcon.tsx`:

```tsx
import type { NavIconName } from './navigation';

interface NavIconProps {
  readonly name: NavIconName;
  readonly size?: number;
}

const glyphs: Record<NavIconName, React.JSX.Element> = {
  sales: (
    <>
      <path d="M6 7h12l-1 12H7L6 7Z" />
      <path d="M9 7a3 3 0 0 1 6 0" />
    </>
  ),
  catalogue: (
    <>
      <path d="M12 3 4 7v10l8 4 8-4V7l-8-4Z" />
      <path d="m4 7 8 4 8-4M12 11v10" />
    </>
  ),
  insights: <path d="M5 20V10M12 20V4M19 20v-7" />,
  operations: (
    <>
      <path d="M4 7h10M18 7h2M4 17h2M10 17h10" />
      <circle cx="16" cy="7" r="2" />
      <circle cx="8" cy="17" r="2" />
    </>
  ),
  account: (
    <>
      <circle cx="12" cy="8" r="4" />
      <path d="M4 20a8 8 0 0 1 16 0" />
    </>
  ),
};

export function NavIcon({ name, size = 20 }: NavIconProps): React.JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      width={size}
      height={size}
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {glyphs[name]}
    </svg>
  );
}
```

Create `src/useNavFold.ts`:

```ts
import { useEffect, useState } from 'react';

const storageKey = 'aiframework.nav.open';

type FoldState = Readonly<Record<string, boolean>>;

function isFoldState(value: unknown): value is FoldState {
  return (
    typeof value === 'object' &&
    value !== null &&
    !Array.isArray(value) &&
    Object.values(value as Record<string, unknown>).every((entry) => typeof entry === 'boolean')
  );
}

function readStored(): FoldState {
  try {
    const raw = window.localStorage.getItem(storageKey);
    if (raw === null) {
      return {};
    }
    const parsed: unknown = JSON.parse(raw);
    return isFoldState(parsed) ? parsed : {};
  } catch {
    // Which groups are open is a convenience, not data. Storage can be blocked (private windows,
    // cleared site data) or hold something this code did not write; either way every group starts
    // closed, the current one still opens, and there is nothing the user could do about it.
    return {};
  }
}

function writeStored(state: FoldState): void {
  try {
    window.localStorage.setItem(storageKey, JSON.stringify(state));
  } catch {
    // Same reasoning as readStored: losing the memory costs the user a click, not their data.
  }
}

export interface NavFold {
  readonly isOpen: (groupId: string) => boolean;
  readonly toggle: (groupId: string) => void;
}

/**
 * Which sidebar groups are open. Closed by default; the group holding the current page opens on
 * first render and again whenever navigation moves into a different group, even if the user had
 * folded it. Staying on a page inside a group the user then folds leaves it folded.
 */
export function useNavFold(activeGroupId: string | undefined): NavFold {
  const [state, setState] = useState<FoldState>(() => ({
    ...readStored(),
    ...(activeGroupId === undefined ? {} : { [activeGroupId]: true }),
  }));
  const [seenGroupId, setSeenGroupId] = useState(activeGroupId);

  // Adjusted during render rather than in an effect: React's own recommendation for state that
  // follows a changing prop, and it leaves no frame in which the new group shows folded.
  if (seenGroupId !== activeGroupId) {
    setSeenGroupId(activeGroupId);
    if (activeGroupId !== undefined) {
      setState({ ...state, [activeGroupId]: true });
    }
  }

  useEffect(() => {
    writeStored(state);
  }, [state]);

  return {
    isOpen: (groupId) => state[groupId] === true,
    toggle: (groupId) => {
      setState((current) => ({ ...current, [groupId]: current[groupId] !== true }));
    },
  };
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `npx vitest run src/useNavFold.test.ts src/NavIcon.test.tsx`
Expected: PASS.

- [ ] **Step 5: Lint, format and commit**

```bash
npx prettier --write src/NavIcon.tsx src/NavIcon.test.tsx src/useNavFold.ts src/useNavFold.test.ts
npx eslint src --max-warnings 0
npx tsc -b
git add src/NavIcon.tsx src/NavIcon.test.tsx src/useNavFold.ts src/useNavFold.test.ts
git commit -m "feat(frontend): add navigation icons and remembered fold state

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The sidebar

**Files:**
- Create: `src/Sidebar.tsx`, `src/Sidebar.css`, `src/Sidebar.test.tsx`

**Interfaces:**
- Consumes: `visibleGroups`, `groupForPath` (Task 1); `NavIcon` is NOT used here (icons are for dashboard tiles); `useNavFold` (Task 2); `Mark` from `src/components/Mark.tsx`; `UserRole`.
- Produces: `Sidebar({ role: UserRole | undefined; open: boolean; onNavigate: () => void })`. Renders `<aside id="app-sidebar">` containing the brand link and `<nav aria-label="Main">`. Group headings are `<button aria-expanded aria-controls>`; each group's pages are a `<ul hidden={!expanded}>`.

- [ ] **Step 1: Write the failing test**

Create `src/Sidebar.test.tsx`:

```tsx
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import type { UserRole } from './features/auth/types';
import { Sidebar } from './Sidebar';

interface Options {
  readonly role?: UserRole;
  readonly onNavigate?: () => void;
}

function renderSidebar(path: string, { role = 'Member', onNavigate = vi.fn() }: Options = {}): void {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Sidebar role={role} open={false} onNavigate={onNavigate} />
    </MemoryRouter>,
  );
}

const group = (name: string): HTMLElement => screen.getByRole('button', { name });

beforeEach(() => {
  window.localStorage.clear();
});
afterEach(() => {
  vi.restoreAllMocks();
});

describe('Sidebar', () => {
  it('labels its navigation "Main", apart from the assistant\'s "Conversations"', () => {
    renderSidebar('/');

    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it('opens the group of the current page and folds the rest', () => {
    renderSidebar('/orders');

    expect(group('Sales')).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('link', { name: 'Place an order' })).toBeInTheDocument();
    expect(group('Catalogue')).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('link', { name: 'Products' })).not.toBeInTheDocument();
  });

  it('opens the group of a deep link, whichever page of it', () => {
    renderSidebar('/monitoring/jobs', { role: 'Admin' });

    expect(group('Operations')).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('link', { name: 'Monitoring' })).toHaveAttribute(
      'aria-current',
      'page',
    );
  });

  it('opens the group of a deep link even when it was folded last time', () => {
    window.localStorage.setItem('aiframework.nav.open', JSON.stringify({ sales: false }));

    renderSidebar('/orders/exports');

    expect(group('Sales')).toHaveAttribute('aria-expanded', 'true');
  });

  it('marks only the exact page, so a sub-page does not light up the list', () => {
    renderSidebar('/orders/exports');

    expect(screen.getByRole('link', { name: 'Exports' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'All orders' })).not.toHaveAttribute('aria-current');
  });

  it('marks the dashboard only at the root', () => {
    renderSidebar('/');
    expect(screen.getByRole('link', { name: 'Dashboard' })).toHaveAttribute('aria-current', 'page');
  });

  it('does not mark the dashboard elsewhere', () => {
    renderSidebar('/orders');
    expect(screen.getByRole('link', { name: 'Dashboard' })).not.toHaveAttribute('aria-current');
  });

  it('folds and unfolds a group on click', async () => {
    renderSidebar('/');

    await userEvent.click(group('Catalogue'));
    expect(group('Catalogue')).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('link', { name: 'Products' })).toBeInTheDocument();

    await userEvent.click(group('Catalogue'));
    expect(group('Catalogue')).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('link', { name: 'Products' })).not.toBeInTheDocument();
  });

  it('marks a folded group that holds the current page with a dot', async () => {
    renderSidebar('/orders');
    expect(document.querySelector('.sidebar__dot')).toBeNull();

    await userEvent.click(group('Sales'));

    expect(document.querySelector('.sidebar__dot')).not.toBeNull();
  });

  it('remembers what was opened when it is mounted again', async () => {
    renderSidebar('/');
    await userEvent.click(group('Catalogue'));
    cleanup();

    renderSidebar('/');

    expect(group('Catalogue')).toHaveAttribute('aria-expanded', 'true');
  });

  it('works when storage refuses', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });

    renderSidebar('/orders');
    expect(group('Sales')).toHaveAttribute('aria-expanded', 'true');

    await userEvent.click(group('Catalogue'));
    expect(group('Catalogue')).toHaveAttribute('aria-expanded', 'true');
  });

  it('gives a member no administrators\' group and no way to their pages', () => {
    renderSidebar('/orders');

    expect(screen.queryByRole('button', { name: 'Operations' })).not.toBeInTheDocument();
    // hidden: true, so a link that is merely folded away would still be found. It is not in the DOM.
    expect(screen.queryByRole('link', { name: 'Fulfilment', hidden: true })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Monitoring', hidden: true })).not.toBeInTheDocument();
  });

  it('gives an administrator the operations group', () => {
    renderSidebar('/orders', { role: 'Admin' });

    expect(group('Operations')).toHaveAttribute('aria-expanded', 'false');
  });

  it('tells its host a link was followed, so the small-screen menu can close', async () => {
    const onNavigate = vi.fn();
    renderSidebar('/orders', { onNavigate });

    await userEvent.click(screen.getByRole('link', { name: 'Exports' }));

    expect(onNavigate).toHaveBeenCalledTimes(1);
  });

  it('links the brand to the dashboard', () => {
    renderSidebar('/orders');

    expect(screen.getByRole('link', { name: 'AI Framework' })).toHaveAttribute('href', '/');
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `npx vitest run src/Sidebar.test.tsx`
Expected: FAIL, cannot resolve `./Sidebar`.

- [ ] **Step 3: Write the implementation**

Create `src/Sidebar.tsx`:

```tsx
import { Link, NavLink, useLocation } from 'react-router-dom';
import { Mark } from './components/Mark';
import type { UserRole } from './features/auth/types';
import { groupForPath, visibleGroups } from './navigation';
import { useNavFold } from './useNavFold';
import './Sidebar.css';

interface SidebarProps {
  readonly role: UserRole | undefined;
  /** Small screens only: whether the overlay is showing. On a wide screen the sidebar is always there. */
  readonly open: boolean;
  /** Called when a link is followed, so the small-screen overlay can close behind it. */
  readonly onNavigate: () => void;
}

export function Sidebar({ role, open, onNavigate }: SidebarProps): React.JSX.Element {
  const { pathname } = useLocation();
  const activeGroupId = groupForPath(pathname)?.id;
  const fold = useNavFold(activeGroupId);

  return (
    <aside id="app-sidebar" className={open ? 'sidebar sidebar--open' : 'sidebar'}>
      <Link className="sidebar__brand" to="/" onClick={onNavigate}>
        <Mark size={28} />
        AI Framework
      </Link>

      {/* "Main", because the assistant page has a second navigation, "Conversations", beside this. */}
      <nav className="sidebar__nav" aria-label="Main">
        <NavLink className="sidebar__link" to="/" end onClick={onNavigate}>
          Dashboard
        </NavLink>

        {visibleGroups(role).map((group) => {
          const expanded = fold.isOpen(group.id);
          const listId = `nav-group-${group.id}`;
          return (
            <div className="sidebar__group" key={group.id}>
              {/* A button, not a heading: the e2e catalogue screen finds `heading "Catalogue"`. */}
              <button
                type="button"
                className="sidebar__heading"
                aria-expanded={expanded}
                aria-controls={listId}
                onClick={() => {
                  fold.toggle(group.id);
                }}
              >
                <span>{group.label}</span>
                {!expanded && group.id === activeGroupId && (
                  <span className="sidebar__dot" aria-hidden="true" />
                )}
                <span className="sidebar__chevron" aria-hidden="true" />
              </button>
              <ul id={listId} className="sidebar__list" hidden={!expanded}>
                {group.items.map((item) => (
                  <li key={item.to}>
                    <NavLink
                      className="sidebar__link sidebar__link--page"
                      to={item.to}
                      end={item.end === true}
                      onClick={onNavigate}
                    >
                      {item.label}
                    </NavLink>
                  </li>
                ))}
              </ul>
            </div>
          );
        })}
      </nav>
    </aside>
  );
}
```

Create `src/Sidebar.css`:

```css
.sidebar {
  position: sticky;
  top: 0;
  display: flex;
  flex-direction: column;
  gap: var(--space-4);
  width: 15rem;
  height: 100dvh;
  overflow-y: auto;
  padding: var(--space-4) var(--space-3);
  border-inline-end: 1px solid var(--color-border);
  background-color: var(--color-surface);
}

.sidebar__brand {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  padding: 0 var(--space-2);
  color: var(--color-text);
  font-size: var(--text-base);
  font-weight: 600;
  letter-spacing: -0.01em;
  text-decoration: none;
}

/* Only the tile is accent-coloured; the wordmark stays body text. */
.sidebar__brand svg {
  color: var(--color-accent);
}

.sidebar__nav {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  font-size: var(--text-sm);
}

.sidebar__link {
  display: block;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-md);
  color: var(--color-text-muted);
  text-decoration: none;
  transition:
    background-color var(--transition),
    color var(--transition);
}

.sidebar__link:hover {
  background-color: var(--color-surface-sunken);
  color: var(--color-text);
}

/* NavLink adds `active` itself; styling it here keeps the component free of className callbacks. */
.sidebar__link.active {
  background-color: var(--color-accent-soft);
  color: var(--color-accent);
  font-weight: 600;
}

.sidebar__link--page {
  padding-inline-start: var(--space-5);
}

.sidebar__group {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  margin-block-start: var(--space-2);
}

.sidebar__heading {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  width: 100%;
  padding: var(--space-2) var(--space-3);
  border: 0;
  border-radius: var(--radius-md);
  background: transparent;
  color: var(--color-text-subtle);
  font: inherit;
  font-size: var(--text-xs);
  font-weight: 600;
  letter-spacing: 0.06em;
  text-align: start;
  text-transform: uppercase;
  cursor: pointer;
  transition: background-color var(--transition);
}

.sidebar__heading:hover {
  background-color: var(--color-surface-sunken);
}

.sidebar__heading:focus-visible,
.sidebar__link:focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: 1px;
}

.sidebar__dot {
  width: 0.375rem;
  height: 0.375rem;
  border-radius: var(--radius-full);
  background-color: var(--color-accent);
}

.sidebar__chevron {
  width: 0.4rem;
  height: 0.4rem;
  margin-inline-start: auto;
  border-inline-end: 2px solid currentColor;
  border-block-end: 2px solid currentColor;
  transform: rotate(45deg);
  transition: transform var(--transition);
}

.sidebar__heading[aria-expanded='false'] .sidebar__chevron {
  transform: rotate(-45deg);
}

.sidebar__list {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  margin: 0;
  padding: 0;
  list-style: none;
}

/* `display: flex` above would otherwise beat the user agent's own [hidden] rule. */
.sidebar__list[hidden] {
  display: none;
}

/* Small screens: an overlay, opened by the shell's menu button. `visibility` as well as the
   transform, so a closed menu's links cannot be tabbed to or read out. */
@media (max-width: 47.99rem) {
  .sidebar {
    position: fixed;
    inset-block: 0;
    inset-inline-start: 0;
    z-index: 30;
    width: min(18rem, 85vw);
    transform: translateX(-100%);
    visibility: hidden;
    transition:
      transform var(--transition),
      visibility var(--transition);
  }

  .sidebar--open {
    transform: none;
    visibility: visible;
    box-shadow: 0 0 2rem rgb(0 0 0 / 20%);
  }
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `npx vitest run src/Sidebar.test.tsx`
Expected: PASS.

- [ ] **Step 5: Lint, format and commit**

```bash
npx prettier --write src/Sidebar.tsx src/Sidebar.css src/Sidebar.test.tsx
npx eslint src --max-warnings 0
npx tsc -b
git add src/Sidebar.tsx src/Sidebar.css src/Sidebar.test.tsx
git commit -m "feat(frontend): add the collapsible grouped sidebar

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Put the sidebar in the shell

**Files:**
- Modify: `src/AppLayout.tsx` (replace whole file), `src/AppLayout.css` (replace whole file), `src/routes.test.tsx:29`
- Test: `src/AppLayout.test.tsx` (new)

**Interfaces:**
- Consumes: `Sidebar` (Task 3), `useSession`, `useLogout`, `NotificationBell`, `AssistantLink`, `useNotificationStream`, `ErrorPanel`.
- Produces: the shell. Menu button (`aria-label="Menu"`, `aria-controls="app-sidebar"`, `aria-expanded`), scrim button (`aria-label="Close menu"`), `main.shell__content` with `shell__content--full` on `/assistant`.

- [ ] **Step 1: Write the failing test**

Create `src/AppLayout.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { withQueryClient } from './test/withQueryClient';
import { AppLayout } from './AppLayout';

function renderShell(path = '/orders'): void {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route element={<AppLayout />}>
          <Route path="*" element={<p>page content</p>} />
        </Route>
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

const menuButton = (): HTMLElement => screen.getByRole('button', { name: 'Menu' });

beforeEach(() => {
  window.localStorage.clear();
});

describe('AppLayout', () => {
  it('renders the page beside the sidebar', () => {
    renderShell();

    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
    expect(screen.getByRole('main')).toHaveTextContent('page content');
  });

  it('keeps the signed-in user, the bell and Sign out in the top strip', async () => {
    renderShell();

    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: /^Notifications/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
  });

  it('lets a full-bleed page, the assistant, fill the viewport', () => {
    renderShell('/assistant');

    expect(screen.getByRole('main')).toHaveClass('shell__content--full');
  });

  it('keeps an ordinary page in the padded column', () => {
    renderShell('/orders');

    expect(screen.getByRole('main')).not.toHaveClass('shell__content--full');
  });

  describe('the small-screen menu', () => {
    it('starts closed and opens from the menu button', async () => {
      renderShell();
      expect(menuButton()).toHaveAttribute('aria-expanded', 'false');
      expect(screen.queryByRole('button', { name: 'Close menu' })).not.toBeInTheDocument();

      await userEvent.click(menuButton());

      expect(menuButton()).toHaveAttribute('aria-expanded', 'true');
      expect(screen.getByRole('button', { name: 'Close menu' })).toBeInTheDocument();
    });

    it('closes from the scrim', async () => {
      renderShell();
      await userEvent.click(menuButton());

      await userEvent.click(screen.getByRole('button', { name: 'Close menu' }));

      expect(menuButton()).toHaveAttribute('aria-expanded', 'false');
    });

    it('closes on Escape and gives focus back to the menu button', async () => {
      renderShell();
      await userEvent.click(menuButton());

      await userEvent.keyboard('{Escape}');

      expect(menuButton()).toHaveAttribute('aria-expanded', 'false');
      expect(menuButton()).toHaveFocus();
    });

    it('closes when a link in it is followed', async () => {
      renderShell();
      await userEvent.click(menuButton());

      await userEvent.click(screen.getByRole('link', { name: 'Exports' }));

      expect(menuButton()).toHaveAttribute('aria-expanded', 'false');
    });
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `npx vitest run src/AppLayout.test.tsx`
Expected: FAIL (`Unable to find an accessible element with the role "navigation" and name "Main"`).

- [ ] **Step 3: Replace `src/AppLayout.tsx`**

```tsx
import { useEffect, useRef, useState } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';
import { ErrorPanel } from './components/ErrorPanel';
import { AssistantLink } from './features/assistant/AssistantLink';
import { useLogout, useSession } from './features/auth/queries';
import { NotificationBell } from './features/notifications/NotificationBell';
import { useNotificationStream } from './features/notifications/stream';
import { Sidebar } from './Sidebar';
import './AppLayout.css';

export function AppLayout(): React.JSX.Element {
  const { data: session } = useSession();
  const logout = useLogout();
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const [menuOpen, setMenuOpen] = useState(false);
  const menuButton = useRef<HTMLButtonElement>(null);
  // The assistant is a full-height, full-width page (its own conversation list + chat); every
  // other screen keeps the centred, padded column.
  const fullBleed = pathname === '/assistant' || pathname.startsWith('/assistant/');

  // Here rather than inside NotificationBell: one connection per signed-in session, not one per
  // component that happens to care. The layout is also the only thing guaranteed mounted for the
  // whole session, so the connection's lifetime matches the session's rather than a screen's.
  // Its error is intentionally not rendered - see NotificationStreamState.error.
  useNotificationStream();

  // Escape closes the small-screen menu and gives focus back to the button that opened it.
  useEffect(() => {
    if (!menuOpen) {
      return undefined;
    }
    function closeOnEscape(event: KeyboardEvent): void {
      if (event.key === 'Escape') {
        setMenuOpen(false);
        menuButton.current?.focus();
      }
    }
    document.addEventListener('keydown', closeOnEscape);
    return () => {
      document.removeEventListener('keydown', closeOnEscape);
    };
  }, [menuOpen]);

  return (
    <div className="shell">
      <Sidebar
        role={session?.role}
        open={menuOpen}
        onNavigate={() => {
          setMenuOpen(false);
        }}
      />
      {menuOpen && (
        <button
          type="button"
          className="shell__scrim"
          tabIndex={-1}
          aria-label="Close menu"
          onClick={() => {
            setMenuOpen(false);
          }}
        />
      )}

      <div className="shell__body">
        <header className="shell__bar">
          <button
            ref={menuButton}
            type="button"
            className="shell__icon shell__menu"
            aria-label="Menu"
            aria-controls="app-sidebar"
            aria-expanded={menuOpen}
            onClick={() => {
              setMenuOpen((open) => !open);
            }}
          >
            <svg
              viewBox="0 0 24 24"
              width="20"
              height="20"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.8"
              strokeLinecap="round"
              aria-hidden="true"
            >
              <path d="M4 7h16M4 12h16M4 17h16" />
            </svg>
          </button>

          <div className="shell__session">
            {/* Inside the layout, so it renders on every signed-in screen and nowhere else - the
                feed is per-user and the query would 401 on /login. */}
            <NotificationBell />
            <AssistantLink />
            {/* RequireAuth guards this subtree, so session is set by the time the shell renders -
                but the query can still be refetching, so this stays defensive rather than
                asserting. */}
            {session && <span className="shell__user">{session.displayName}</span>}
            <button
              className="btn btn--secondary"
              type="button"
              disabled={logout.isPending}
              onClick={() => {
                logout.mutate(undefined, { onSuccess: () => void navigate('/login') });
              }}
            >
              {logout.isPending && <span className="spinner" aria-hidden="true" />}
              Sign out
            </button>
          </div>
        </header>

        {logout.error && <ErrorPanel error={logout.error} className="shell__error" />}

        <main className={fullBleed ? 'shell__content shell__content--full' : 'shell__content'}>
          <Outlet />
        </main>
      </div>
    </div>
  );
}
```

- [ ] **Step 4: Replace `src/AppLayout.css`**

```css
.shell {
  display: grid;
  grid-template-columns: 15rem minmax(0, 1fr);
  min-height: 100dvh;
  background-color: var(--color-canvas);
}

.shell__body {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.shell__bar {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  padding: var(--space-3) var(--space-5);
  border-block-end: 1px solid var(--color-border);
  background-color: var(--color-surface);
}

.shell__session {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  margin-inline-start: auto;
}

.shell__user {
  color: var(--color-text-muted);
  font-size: var(--text-sm);
}

.shell__error {
  max-width: 64rem;
  margin: var(--space-5) auto 0;
}

.shell__content {
  width: 100%;
  max-width: 64rem;
  margin: 0 auto;
  padding: var(--space-6) var(--space-5) var(--space-8);
}

/* The header's icon links (the assistant) and the menu button, shaped like the notification bell. */
.shell__icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 2.25rem;
  height: 2.25rem;
  border-radius: var(--radius-md, 8px);
  color: var(--color-text-muted);
}

.shell__icon:hover {
  color: var(--color-accent);
  background: var(--color-surface-raised, rgb(0 0 0 / 4%));
}

.shell__icon--active {
  color: var(--color-accent);
  background: var(--color-accent-soft);
}

.shell__menu {
  display: none;
  border: 0;
  background: transparent;
  cursor: pointer;
}

.shell__scrim {
  display: none;
}

/* A full-bleed page fills the viewport below the bar, so only its own panes scroll. */
.shell:has(.shell__content--full) {
  height: 100dvh;
  grid-template-rows: minmax(0, 1fr);
}

.shell__content--full {
  flex: 1;
  min-height: 0;
  max-width: none;
  margin: 0;
  padding: 0;
  display: flex;
}

/* Small screens: one column. The sidebar leaves the grid (it is fixed, see Sidebar.css) and the
   menu button and scrim appear. */
@media (max-width: 47.99rem) {
  .shell {
    grid-template-columns: minmax(0, 1fr);
  }

  .shell__bar {
    padding-inline: var(--space-4);
  }

  .shell__menu {
    display: inline-flex;
  }

  .shell__scrim {
    display: block;
    position: fixed;
    inset: 0;
    z-index: 20;
    border: 0;
    background: rgb(0 0 0 / 40%);
  }

  .shell__content {
    padding-inline: var(--space-4);
  }
}
```

- [ ] **Step 5: Update the one existing test the move breaks**

In `src/routes.test.tsx`, the catalogue page's nav entry is now called "Products" (the word "Catalogue" is a group button). Change line 29 from

```tsx
    expect(screen.getByRole('link', { name: 'Catalogue' })).toBeInTheDocument();
```
to
```tsx
    expect(screen.getByRole('link', { name: 'Products' })).toBeInTheDocument();
```

- [ ] **Step 6: Run the whole unit suite**

Run: `npx vitest run`
Expected: PASS, including `AppLayout.test.tsx`, `routes.test.tsx` and `App.test.tsx` (`/` still redirects to `/orders` at this point).

- [ ] **Step 7: Lint, format and commit**

```bash
npx prettier --write src/AppLayout.tsx src/AppLayout.css src/AppLayout.test.tsx src/routes.test.tsx
npx eslint . --max-warnings 0
npx tsc -b
git add src/AppLayout.tsx src/AppLayout.css src/AppLayout.test.tsx src/routes.test.tsx
git commit -m "feat(frontend): move the navigation into a left sidebar

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The dashboard page

**Files:**
- Create: `src/features/dashboard/DashboardPage.tsx`, `src/features/dashboard/dashboard.css`, `src/features/dashboard/DashboardPage.test.tsx`

**Interfaces:**
- Consumes: `visibleGroups` (Task 1), `NavIcon` (Task 2), `useSession` from `src/features/auth/queries.ts`.
- Produces: `DashboardPage()`: `<h1>Dashboard</h1>`, a greeting paragraph, then one `<section aria-labelledby>` per visible group with an `<h2>` and a list of subtile `<Link>`s. A subtile link's accessible name is `"<page>, in <group>"` so that it never collides with the sidebar's link of the same page name.

- [ ] **Step 1: Write the failing test**

Create `src/features/dashboard/DashboardPage.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { anAdminSession, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { DashboardPage } from './DashboardPage';

function renderPage(): void {
  render(
    <MemoryRouter initialEntries={['/']}>
      <DashboardPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

const tileTitles = (): (string | null)[] =>
  screen.getAllByRole('heading', { level: 2 }).map((heading) => heading.textContent);

describe('DashboardPage', () => {
  it('has one tile for each group a member can use', () => {
    renderPage();

    expect(screen.getByRole('heading', { name: 'Dashboard', level: 1 })).toBeInTheDocument();
    expect(tileTitles()).toEqual(['Sales', 'Catalogue', 'Insights', 'Account']);
  });

  it('lists a group\'s pages as subtiles that link to them', () => {
    renderPage();

    expect(screen.getByRole('link', { name: 'All orders, in Sales' })).toHaveAttribute('href', '/orders');
    expect(screen.getByRole('link', { name: 'Place an order, in Sales' })).toHaveAttribute('href', '/orders/new');
    expect(screen.getByRole('link', { name: 'Exports, in Sales' })).toHaveAttribute('href', '/orders/exports');
    expect(screen.getByRole('link', { name: 'Products, in Catalogue' })).toHaveAttribute('href', '/products');
    expect(screen.getByRole('link', { name: 'Population, in Insights' })).toHaveAttribute('href', '/statistics/population');
    expect(screen.getByRole('link', { name: 'Password, in Account' })).toHaveAttribute('href', '/account/password');
  });

  it('says what each page is for', () => {
    renderPage();

    expect(screen.getByText('Download your orders as a PDF')).toBeInTheDocument();
  });

  it('greets the signed-in user by name', async () => {
    renderPage();

    expect(await screen.findByText(/Welcome back, Ada Lovelace/)).toBeInTheDocument();
  });

  it('gives a member no operations tile, and no link to its pages', async () => {
    renderPage();
    await screen.findByText(/Welcome back, Ada Lovelace/);

    expect(screen.queryByRole('heading', { name: 'Operations' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Monitoring/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Fulfilment/ })).not.toBeInTheDocument();
  });

  it('adds the operations tile once an administrator\'s session arrives', async () => {
    server.use(http.get('/api/auth/me', () => HttpResponse.json(anAdminSession)));
    renderPage();

    expect(await screen.findByRole('heading', { name: 'Operations', level: 2 })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Monitoring, in Operations' })).toHaveAttribute('href', '/monitoring');
    expect(screen.getByRole('link', { name: 'Fulfilment, in Operations' })).toHaveAttribute('href', '/fulfilment');
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `npx vitest run src/features/dashboard/DashboardPage.test.tsx`
Expected: FAIL, cannot resolve `./DashboardPage`.

- [ ] **Step 3: Write the implementation**

Create `src/features/dashboard/DashboardPage.tsx`:

```tsx
import { Link } from 'react-router-dom';
import { NavIcon } from '../../NavIcon';
import { visibleGroups } from '../../navigation';
import { useSession } from '../auth/queries';
import './dashboard.css';

/**
 * The landing page: a tile per navigation group, a subtile per page in it. Built from the same
 * config as the sidebar, so the two cannot disagree. The only data is the session, which
 * RequireAuth has already resolved, so there is no query here and no error state to render.
 */
export function DashboardPage(): React.JSX.Element {
  const { data: session } = useSession();

  return (
    <div className="dashboard">
      <h1>Dashboard</h1>
      <p className="dashboard__lead">
        {session ? `Welcome back, ${session.displayName}. ` : ''}Jump to anything in the framework.
      </p>

      <div className="dashboard__grid">
        {visibleGroups(session?.role).map((group) => {
          const titleId = `dashboard-tile-${group.id}`;
          return (
            <section className="dashboard__tile" key={group.id} aria-labelledby={titleId}>
              <h2 id={titleId} className="dashboard__title">
                <span className="dashboard__icon">
                  <NavIcon name={group.icon} />
                </span>
                {group.label}
              </h2>
              <p className="dashboard__description">{group.description}</p>
              <ul className="dashboard__subs">
                {group.items.map((item) => (
                  <li key={item.to}>
                    {/* Named with its group so it never shares a name with the sidebar's link to
                        the same page: Playwright's getByRole is strict. */}
                    <Link
                      className="dashboard__sub"
                      to={item.to}
                      aria-label={`${item.label}, in ${group.label}`}
                    >
                      <span className="dashboard__sub-label">{item.label}</span>
                      <span className="dashboard__sub-description">{item.description}</span>
                    </Link>
                  </li>
                ))}
              </ul>
            </section>
          );
        })}
      </div>
    </div>
  );
}
```

Create `src/features/dashboard/dashboard.css`:

```css
.dashboard__lead {
  margin-block: var(--space-1) var(--space-5);
  color: var(--color-text-muted);
}

.dashboard__grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-4);
}

@media (min-width: 40rem) {
  .dashboard__grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

/* The content column is capped at 64rem, so three across only pays off on a wide screen. */
@media (min-width: 80rem) {
  .dashboard__grid {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

.dashboard__tile {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  padding: var(--space-4);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background-color: var(--color-surface);
}

.dashboard__title {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  margin: 0;
  font-size: var(--text-base);
}

.dashboard__icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 2rem;
  height: 2rem;
  border-radius: var(--radius-md);
  background-color: var(--color-accent-soft);
  color: var(--color-accent);
}

.dashboard__description {
  margin: 0 0 var(--space-2);
  color: var(--color-text-muted);
  font-size: var(--text-sm);
}

.dashboard__subs {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  margin: 0;
  padding: 0;
  list-style: none;
}

.dashboard__sub {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  padding: var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  background-color: var(--color-surface-sunken);
  color: var(--color-text);
  text-decoration: none;
  transition:
    border-color var(--transition),
    background-color var(--transition);
}

.dashboard__sub:hover {
  border-color: var(--color-accent);
  background-color: var(--color-accent-soft);
}

.dashboard__sub:focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: 1px;
}

.dashboard__sub-label {
  font-size: var(--text-sm);
  font-weight: 600;
}

.dashboard__sub-description {
  color: var(--color-text-muted);
  font-size: var(--text-xs);
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `npx vitest run src/features/dashboard/DashboardPage.test.tsx`
Expected: PASS.

- [ ] **Step 5: Lint, format and commit**

```bash
npx prettier --write src/features/dashboard
npx eslint src --max-warnings 0
npx tsc -b
git add src/features/dashboard
git commit -m "feat(frontend): add the dashboard page

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Make the dashboard the starting page

**Files:**
- Modify: `src/routes.tsx:39`, `src/features/auth/LoginPage.tsx:20`, `src/features/auth/RegisterPage.tsx:22`, `src/App.test.tsx`, `src/features/auth/LoginPage.test.tsx:19-28,43`, `src/routes.test.tsx` (add cases)

**Interfaces:**
- Consumes: `DashboardPage` (Task 5).
- Produces: `/` renders the dashboard; a successful sign-in or registration navigates to `/`.

- [ ] **Step 1: Write the failing tests**

In `src/routes.test.tsx`, add inside `describe('AppRoutes', …)`, after the `'renders the orders screens inside the app shell'` case:

```tsx
  it('renders the dashboard at the root, with the sidebar beside it', async () => {
    renderAt('/');

    expect(await screen.findByRole('heading', { name: 'Dashboard', level: 1 })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Dashboard' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Exports, in Sales' })).toHaveAttribute(
      'href',
      '/orders/exports',
    );
  });

  it('keeps the operations group out of a member\'s sidebar and dashboard', async () => {
    renderAt('/');
    await screen.findByText(/Welcome back, Ada Lovelace/);

    expect(screen.queryByRole('button', { name: 'Operations' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Operations' })).not.toBeInTheDocument();
  });

  it('shows an administrator the operations group in both', async () => {
    server.use(http.get('/api/auth/me', () => HttpResponse.json(anAdminSession)));
    renderAt('/');

    expect(await screen.findByRole('button', { name: 'Operations' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Operations', level: 2 })).toBeInTheDocument();
  });
```

In `src/App.test.tsx`, replace the test body's assertion (line 17) with:

```tsx
    expect(await screen.findByRole('heading', { name: 'Dashboard', level: 1 })).toBeInTheDocument();
```
and rename the test to `'lands on the dashboard once the session resolves'`.

In `src/features/auth/LoginPage.test.tsx`: change the comment and routes at lines 19-28 to

```tsx
// Exercises the real route swap (/login -> /) the way the app renders it; a bare
// MemoryRouter with no <Routes> lets navigate() go nowhere, which would let a broken redirect
// pass this test.
function renderPageWithRoutes(): void {
  render(
    <MemoryRouter initialEntries={['/login']}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<p>the dashboard</p>} />
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}
```
(keep the `render(...)` call's remaining arguments exactly as they are), rename the test at line 43 to `'sends the typed credentials to the api and lands on the dashboard'`, replace line 62 with

```tsx
    expect(await screen.findByText('the dashboard')).toBeInTheDocument();
```
and delete the now-unused `import { OrderList } from '../orders/OrderList';` (line 7).

- [ ] **Step 2: Run them to verify they fail**

Run: `npx vitest run src/routes.test.tsx src/App.test.tsx src/features/auth/LoginPage.test.tsx`
Expected: FAIL (the root still redirects to `/orders`; login still navigates to `/orders`).

- [ ] **Step 3: Make the changes**

`src/routes.tsx`: add `import { DashboardPage } from './features/dashboard/DashboardPage';` in alphabetical position (after the `auth` imports, before `fulfilment`), and replace line 39

```tsx
          <Route path="/" element={<Navigate to="/orders" replace />} />
```
with
```tsx
          <Route path="/" element={<DashboardPage />} />
```
(`Navigate` stays imported: the `/monitoring/assistant` redirect uses it.)

`src/features/auth/LoginPage.tsx` line 20 and `src/features/auth/RegisterPage.tsx` line 22: change `void navigate('/orders')` to `void navigate('/')`.

- [ ] **Step 4: Run the whole unit suite**

Run: `npx vitest run`
Expected: PASS.

- [ ] **Step 5: Lint, format and commit**

```bash
npx prettier --write src/routes.tsx src/routes.test.tsx src/App.test.tsx src/features/auth
npx eslint . --max-warnings 0
npx tsc -b
git add src
git commit -m "feat(frontend): land on the dashboard after signing in

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: End-to-end tests

**Files:**
- Create: `e2e/screens/dashboard.ts`, `e2e/specs/navigation/dashboard.spec.ts`
- Modify: `e2e/screens/shell.ts`, `e2e/screens/monitoring.ts:15`, `e2e/screens/fulfilment.ts:6-7`, `e2e/screens/exports.ts:24-25`, `e2e/specs/orders/exports.spec.ts:2,25`, `e2e/specs/orders/fulfilment.spec.ts:2,33`, `e2e/specs/auth/navigation.spec.ts:1-13`, `e2e/specs/auth/sign-in.spec.ts:17,39`, `e2e/specs/auth/registration.spec.ts:16-19`, `e2e/specs/auth/change-password.spec.ts:63`

**Interfaces:**
- Consumes: the sidebar (`navigation "Main"`, group buttons with `aria-expanded`, links) and dashboard (`heading "Dashboard"`, `region` per tile, links named `"<page>, in <group>"`).
- Produces: `shell.mainNav`, `shell.navLink(p, label)`, `shell.groupButton(p, group)`, `shell.goTo(p, group, label)`; `dashboard.heading`, `dashboard.tile`, `dashboard.subtile`.

- [ ] **Step 1: Shell and dashboard screens**

Append to `e2e/screens/shell.ts`:

```ts

/** The sidebar. Named "Main" so it is not confused with the assistant's "Conversations" navigation. */
export const mainNav = (p: Page): Locator => p.getByRole('navigation', { name: 'Main' });

/** A page's link in the sidebar. Not visible while its group is folded. */
export const navLink = (p: Page, label: string): Locator =>
  mainNav(p).getByRole('link', { name: label, exact: true });

/** A group's fold button. Absent, not just folded, for a group the user may not see. */
export const groupButton = (p: Page, group: string): Locator =>
  mainNav(p).getByRole('button', { name: group, exact: true });

/** Unfolds the group if it is folded, then follows the link. */
export async function goTo(p: Page, group: string, label: string): Promise<void> {
  const button = groupButton(p, group);
  if ((await button.getAttribute('aria-expanded')) !== 'true') {
    await button.click();
  }
  await navLink(p, label).click();
}
```

Create `e2e/screens/dashboard.ts`:

```ts
import type { Locator, Page } from '@playwright/test';

export const heading = (p: Page): Locator => p.getByRole('heading', { name: 'Dashboard', level: 1 });

/** A group's tile: a region named by its title. */
export const tile = (p: Page, group: string): Locator =>
  p.getByRole('region', { name: group, exact: true });

/** One page's subtile inside a tile. Named with its group, which keeps it apart from the sidebar's link. */
export const subtile = (p: Page, group: string, label: string): Locator =>
  tile(p, group).getByRole('link', { name: `${label}, in ${group}`, exact: true });
```

- [ ] **Step 2: Point the three `navLink` helpers at the sidebar**

`e2e/screens/monitoring.ts`: add `import * as shell from './shell.ts';` under the existing `import type …` line, and replace line 15 with

```ts
export const navLink = (p: Page): Locator => shell.navLink(p, 'Monitoring');
```
`e2e/screens/fulfilment.ts`: same import; replace line 7 with

```ts
export const navLink = (p: Page): Locator => shell.navLink(p, 'Fulfilment');
```
`e2e/screens/exports.ts`: delete lines 24-25 (the `/** The header's nav entry … */` comment and its `navLink`); the spec uses `shell.goTo` instead.

- [ ] **Step 3: Fix the call sites that click a nav link**

`e2e/specs/orders/exports.spec.ts`: add `import * as shell from '../../screens/shell.ts';` after the `exportsPage` import (line 3) and replace line 25 with

```ts
      await shell.goTo(signedInPage, 'Sales', 'Exports');
```
`e2e/specs/orders/fulfilment.spec.ts`: add the same `shell` import after the `fulfilment` import (line 2) and replace line 33 with

```ts
      await shell.goTo(adminPage, 'Operations', 'Fulfilment');
```
The assertions at `fulfilment.spec.ts:19` and `monitoring.spec.ts:89` keep using `navLink`; they now look in the sidebar, where a member has no such link.

- [ ] **Step 4: Fix the assertions that expected to land on `/orders`**

`e2e/specs/auth/navigation.spec.ts`: replace the imports and first test (lines 1-13) with

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as dashboard from '../../screens/dashboard.ts';
import * as login from '../../screens/login.ts';
import * as register from '../../screens/register.ts';

// None of these submits a form, so none spends an auth rate-limit permit - they run everywhere.

test('lands a signed-in visitor on the dashboard at the root', async ({ signedInPage }) => {
  await signedInPage.goto('/');

  await expect(signedInPage).toHaveURL(/\/$/);
  await expect(dashboard.heading(signedInPage)).toBeVisible();
});
```
(the remaining tests in the file are unchanged).

`e2e/specs/auth/sign-in.spec.ts`: add `import * as dashboard from '../../screens/dashboard.ts';` after the `login` import; replace line 17 and line 39 (`expect(orders.pageHeading(…)).toBeVisible()` straight after a sign-in) with `await expect(dashboard.heading(signedInPage)).toBeVisible();` and `await expect(dashboard.heading(page)).toBeVisible();` respectively. Line 61 follows `goto('/orders')` and stays.

`e2e/specs/auth/registration.spec.ts`: add the `dashboard` import; replace lines 16-19 (the comment, `waitForURL('**/orders')` and the `orders.pageHeading` assertion) with

```ts
  // Registration signs in and lands on the dashboard; waiting for that is what makes this a real
  // end-to-end check of the cookie rather than of the form alone.
  await page.waitForURL((url) => url.pathname === '/');
  await expect(dashboard.heading(page)).toBeVisible();
```
and remove the `orders` import if nothing else in the file uses it.

`e2e/specs/auth/change-password.spec.ts` line 63 (after `login.signIn`): replace `orders.pageHeading(isolatedPage)` with `dashboard.heading(isolatedPage)` and add the `dashboard` import. Lines 32 and 79 follow `goto('/orders')` and stay.

- [ ] **Step 5: The new spec**

Create `e2e/specs/navigation/dashboard.spec.ts`:

```ts
import { expect, test } from '../../fixtures/index.ts';
import * as dashboard from '../../screens/dashboard.ts';
import * as monitoring from '../../screens/monitoring.ts';
import * as orders from '../../screens/orders.ts';
import * as shell from '../../screens/shell.ts';

// Runs as workerUser / adminPage, so none of it spends an auth permit.

test('offers a member a tile for each of their groups, and no more', async ({ signedInPage }) => {
  await signedInPage.goto('/');

  await expect(dashboard.heading(signedInPage)).toBeVisible();
  for (const group of ['Sales', 'Catalogue', 'Insights', 'Account']) {
    await expect(dashboard.tile(signedInPage, group)).toBeVisible();
  }
  await expect(dashboard.tile(signedInPage, 'Operations')).toHaveCount(0);
  await expect(shell.groupButton(signedInPage, 'Operations')).toHaveCount(0);
});

test('reaches a page through its subtile, and opens that page\'s group', async ({
  signedInPage,
}) => {
  await signedInPage.goto('/');

  await dashboard.subtile(signedInPage, 'Sales', 'All orders').click();

  await expect(orders.pageHeading(signedInPage)).toBeVisible();
  await expect(shell.groupButton(signedInPage, 'Sales')).toHaveAttribute('aria-expanded', 'true');
});

test('opens the current page\'s group and folds and unfolds the others', async ({
  signedInPage,
}) => {
  await signedInPage.goto('/orders');

  await expect(shell.groupButton(signedInPage, 'Sales')).toHaveAttribute('aria-expanded', 'true');
  await expect(shell.groupButton(signedInPage, 'Catalogue')).toHaveAttribute('aria-expanded', 'false');
  await expect(shell.navLink(signedInPage, 'Products')).toBeHidden();

  await shell.groupButton(signedInPage, 'Catalogue').click();

  await expect(shell.navLink(signedInPage, 'Products')).toBeVisible();
});

test.describe('for the operator', { tag: '@local-only' }, () => {
  test('adds the operations tile, and the sidebar reaches monitoring', async ({ adminPage }) => {
    await adminPage.goto('/');

    await expect(dashboard.tile(adminPage, 'Operations')).toBeVisible();

    await shell.goTo(adminPage, 'Operations', 'Monitoring');
    await expect(monitoring.overviewHeading(adminPage)).toBeVisible();
  });
});
```

- [ ] **Step 6: Typecheck and lint the e2e code**

Run: `npx tsc -p tsconfig.node.json --noEmit && npx eslint e2e --max-warnings 0`
Expected: no output.

- [ ] **Step 7: Run the whole e2e suite**

Stop the dev loop first (`./scripts/stop-dev.ps1` from the repo root); `npm run e2e` starts its own stack on 5234 and 5235.
Run: `npm run e2e`
Expected: all tests pass. If a test fails with `strict mode violation … resolved to 2 elements` for a link, a locator is matching both the sidebar's new link ("Products", "Dashboard", "Password") and something on the page; scope it to the page's own landmark (`p.getByRole('main').getByRole('link', …)`) in the `screens/*.ts` helper, not in the spec. Any other failure is a real regression: stop and diagnose before continuing.

- [ ] **Step 8: Update the counts in `e2e/CLAUDE.md`, then commit**

Run `npx playwright test --list` to get the totals; the "A `kind` run therefore executes 28 of the 61 tests" sentence becomes 31 of 65 (three new untagged tests, one new `@local-only`) unless the list says otherwise; use the list's numbers. Add a row to "What is covered": `| Navigation | dashboard tiles per role, subtile navigation, sidebar groups fold and unfold, operator reaches monitoring through the sidebar |`. Under "Screens", add the bullet:

```
- **The sidebar folds.** A page's nav link is not visible while its group is closed, so a spec that
  clicks one uses `shell.goTo(page, 'Group', 'Page')`, which opens the group first. Assertions that a
  link is absent use `shell.navLink`/`shell.groupButton` with `toHaveCount(0)`/`toBeHidden()`.
```

```bash
npx prettier --write e2e
git add e2e
git commit -m "feat(frontend): cover the sidebar and the dashboard end to end

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Document, verify, review, open the PR

**Files:**
- Create: `docs/adr/0035-grouped-sidebar-navigation-with-a-dashboard-landing-page.md`
- Modify: `frontend/CLAUDE.md`, `docs/superpowers/specs/2026-10-12-sidebar-navigation-and-dashboard-design.md`

- [ ] **Step 1: Record the decision**

Run the `adr` skill (from the repo root) with the title `Grouped sidebar navigation with a dashboard landing page`; it fixes the file name `docs/adr/0035-grouped-sidebar-navigation-with-a-dashboard-landing-page.md` and the template (`# 0035. …`, **Date** 2026-10-12, **Status** Accepted, Context, Decision, Consequences, as in `docs/adr/0034-…`). Content to put in it:

- *Context:* eight flat links in a top bar that grows with every feature; two are administrator-only; a landing page that was just the orders list. Design: the spec path.
- *Decision:* (1) `src/navigation.ts` is the single source of groups and pages; the sidebar and the dashboard both render from it, and a page not listed there is reachable only by URL. (2) Groups fold; the open set is remembered per browser in `localStorage` (never server-side, no API); the group holding the current page opens on navigation; storage failures degrade to closed. (3) Administrator groups are filtered in `visibleGroups` as cosmetics only; `RequireRole` and the API policies stay the control (ADR 0020). (4) `/` is the dashboard, and sign-in and registration land there. (5) Group headings are buttons, not headings, and dashboard subtile links carry the group in their accessible name, so neither collides with page headings or the sidebar's links in role-based locators. (6) Below 48rem the sidebar is an overlay.
- *Consequences:* a new page needs one entry in `navigation.ts`; e2e specs reach a page through `shell.goTo`; the assistant page shows two navigations (the sidebar and its conversation list); live figures on tiles, an icon-rail desktop sidebar and favourites are deliberately not built.

- [ ] **Step 2: Add the rule to `frontend/CLAUDE.md`**

Insert a section before "## The API proxy":

```markdown
## Navigation

`src/navigation.ts` is the one list of groups and pages. The sidebar and the dashboard both render
from it, so **a new page is added there or it is reachable only by URL.** Administrator groups set
`adminOnly`; that hides the group and nothing else — the route still needs `RequireRole` in
`routes.tsx` and the API its policy.

Group headings in the sidebar are `<button>`s, never headings (the e2e catalogue screen finds
`heading "Catalogue"`), and a dashboard subtile's accessible name is `"<page>, in <group>"` so it
never shares a name with the sidebar's link to the same page. Which groups are open is remembered in
`localStorage` (`aiframework.nav.open`); every access is in try/catch and the UI works without it.
Specs reach a page with `shell.goTo` (`e2e/screens/shell.ts`), which unfolds its group first.
```

- [ ] **Step 3: Bring the spec in line with what was built**

In `docs/superpowers/specs/2026-10-12-sidebar-navigation-and-dashboard-design.md` make these five corrections: (a) under "Dashboard", `Route / renders the page … Login still lands on /` becomes: *"`LoginPage` and `RegisterPage` navigate to `/` (they hard-coded `/orders`)"*; (b) the greeting is a paragraph under an `<h1>Dashboard</h1>`, not a "Welcome back" heading (the login page's heading is "Welcome back"); (c) the grid is one column, two from 40rem, three from 80rem; (d) the sidebar's group headings are buttons and its navigation is labelled "Main"; there are no heading elements in it; (e) `visibleGroups` drops admin groups only, since no group is ever empty.

- [ ] **Step 4: Run the `verify` skill**

Invoke the `verify` skill. It builds, tests and lints both stacks and runs the codegen and contract diffs. Expected: everything green, and **no diff** in `openapi/AiFramework.Api.json`, `frontend/src/api/schema.d.ts` or the two `Internal/Generated` trees (this change touches none of them). Report what actually ran.

- [ ] **Step 5: Look at it in a browser, at the widths unit tests cannot judge**

Run `./scripts/dev.ps1` from the repo root and open http://localhost:5173. Check, signed in as a member and as an administrator (`Admin__Usernames`, see the `local-dev` skill):
1. The dashboard shows the right tiles; light and dark (`prefers-color-scheme`) both read well.
2. Sidebar groups fold and unfold, the dot appears on a folded group holding the current page, a reload keeps the state.
3. At 1280px, 800px and 500px wide the layout holds; at 500px the menu button, overlay, scrim, Esc and link-follow all close it.
4. `/assistant` at 800px and 1280px: the sidebar plus the assistant's own conversation list plus the chat all fit and only the panes scroll.

Fix anything found, with a test where a test can catch it. A dev server started from the agent is reaped between turns (see memory): tell the user what was and was not checked rather than implying a running session.

- [ ] **Step 6: Review**

Dispatch the `react-reviewer` agent on the branch diff (`git diff main...HEAD -- frontend`) and fix what it raises.

- [ ] **Step 7: Commit the docs, push, open the PR**

```bash
git add docs frontend/CLAUDE.md
git commit -m "docs(frontend): record the sidebar navigation decision

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
git push -u origin claude/sidebar-navigation-dashboard
gh pr create --base main --title "feat(frontend): grouped sidebar navigation and a dashboard landing page" --body "<summary, test plan, screenshots>"
```
End the PR body with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Do not hand-apply labels. Confirm with the user before pushing, since it publishes the branch.

---

## Self-Review

**Spec coverage.** Nav config and `visibleGroups`/`groupForPath` → Task 1. Icons, fold persistence, forced-open on navigation, storage failures → Task 2. Sidebar markup, `aria-label="Main"`, buttons not headings, dot, `aria-current`/`end` → Task 3. Shell grid, top strip, small-screen overlay, Esc/focus, scrim, full-bleed assistant → Task 4. Dashboard tiles, subtile names, member/admin, session-not-loaded → Task 5. `/` route, login and register landing, existing-test updates → Task 6. The three "silent break" items (duplicate names, folded links, landing page) → Tasks 5, 7, 6. E2E helpers and new spec → Task 7. ADR, CLAUDE.md, spec sync, verify, manual width check including `/assistant`, review, PR → Task 8. Out-of-scope items are not built.

**Placeholders.** None left; every code step carries its code. Task 7 step 8 and Task 8 step 7 contain `<…>` only for numbers read from a command's output and for the PR body, which depend on results not yet known.

**Type consistency.** `NavIconName`, `NavItem`, `NavGroup`, `navGroups`, `visibleGroups`, `groupForPath` (Task 1) are used with those names in Tasks 2, 3, 5. `useNavFold(...)` returns `{ isOpen, toggle }` (Task 2), used as `fold.isOpen`/`fold.toggle` in Task 3. `Sidebar` props `role`, `open`, `onNavigate` (Task 3) match `AppLayout` (Task 4). Accessible names — `"<page>, in <group>"` (Task 5) — match `dashboard.subtile` (Task 7) and the routes tests (Task 6). Group ids and storage key `aiframework.nav.open` match across Tasks 1-3.
