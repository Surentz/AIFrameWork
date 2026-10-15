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
      {
        label: 'Place an order',
        to: '/orders/new',
        description: 'Order something from the catalogue',
      },
      { label: 'Exports', to: '/orders/exports', description: 'Download your orders as a PDF' },
    ],
  },
  {
    id: 'catalogue',
    label: 'Catalogue',
    description: 'The product catalogue',
    icon: 'catalogue',
    items: [{ label: 'Products', to: '/products', end: true, description: 'Browse the catalogue' }],
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
      {
        label: 'Assistant insights',
        to: '/monitoring/assistant-insights',
        description: 'What the assistant answered, and what it missed',
      },
    ],
  },
  {
    id: 'account',
    label: 'Account',
    description: 'Your sign-in',
    icon: 'account',
    items: [{ label: 'Password', to: '/account/password', description: 'Change your password' }],
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
