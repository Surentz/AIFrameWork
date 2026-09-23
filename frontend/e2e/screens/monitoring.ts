import type { Locator, Page } from '@playwright/test';

export const overviewHeading = (p: Page): Locator =>
  p.getByRole('heading', { name: 'Monitoring', level: 1 });

/** A monitoring sub-page's <h1>: 'Traffic', 'Job runs', 'Sign-ins', 'Users'. */
export const pageHeading = (p: Page, name: string): Locator =>
  p.getByRole('heading', { name, level: 1 });

/** The traffic page's per-endpoint table, rendered whether or not a bucket has been flushed. */
export const endpointBreakdownHeading = (p: Page): Locator =>
  p.getByRole('heading', { name: 'By endpoint and handler', level: 2 });

/** The shell's nav entry, rendered only for an administrator. */
export const navLink = (p: Page): Locator => p.getByRole('link', { name: 'Monitoring' });

/** A tile group on the overview, each labelled by what it counts. */
export const tiles = (p: Page, label: string): Locator => p.getByRole('list', { name: label });

/**
 * One tile's number. The markup is `<li class="tile"><span class="tile__value">3</span>
 * <span class="tile__label">Failed</span></li>`, and neither span carries a role that pairs it
 * with the other — so this pairs them structurally, the same way `orders.fact` does.
 */
export const tile = (p: Page, group: string, label: string): Locator =>
  tiles(p, group)
    .locator('li.tile')
    .filter({ has: p.getByText(label, { exact: true }) })
    .locator('.tile__value');

export const drillDown = (p: Page, name: string | RegExp): Locator =>
  p.getByRole('link', { name });

/** The refusal `RequireRole` renders in place of the page. See ADR 0020. */
export const refusal = (p: Page): Locator => p.getByRole('alert');

/** A chart's `<figcaption>`, which is also the start of its `role="img"` accessible name. */
export const chartTitle = (p: Page, title: string): Locator =>
  p.locator('figure.viz').filter({ hasText: title }).locator('figcaption');

export const windowFilter = (p: Page): Locator => p.getByLabel('Window');

/** The user-management table row for one account, located by its username cell. */
export const userRow = (p: Page, username: string): Locator =>
  p.locator('table.runs tbody tr').filter({ has: p.getByRole('cell', { name: username, exact: true }) });

export const userSearch = (p: Page): Locator => p.getByLabel('Search');

export type UserAction = 'Promote' | 'Demote' | 'Sign out' | 'History' | 'Confirm' | 'Cancel';

/** One of a user row's buttons. Scoped to the row, so the shell's own "Sign out" never matches. */
export const userAction = (p: Page, username: string, action: UserAction): Locator =>
  userRow(p, username).getByRole('button', { name: action, exact: true });

/**
 * The inline confirmation, a `role="group"` named by its question - "Promote <name> to Admin?".
 * The question names the account in words, so a misclick cannot act on the wrong person.
 */
export const confirmation = (p: Page, question: string): Locator =>
  p.getByRole('group', { name: question });

/** The audit table the History button opens, captioned with the account it describes. */
export const userHistory = (p: Page, username: string): Locator =>
  p.getByRole('table', { name: `What has been done to ${username}` });

/** Finds an account by username, which is also how an operator would. */
export async function findUser(p: Page, username: string): Promise<void> {
  await userSearch(p).fill(username);
  await p.getByRole('button', { name: 'Search' }).click();
  await userRow(p, username).waitFor();
}

/**
 * Promotes or demotes an account and waits for the change to land: the row offers the opposite
 * action only once the confirmation has closed on success.
 */
export async function changeRole(
  p: Page,
  username: string,
  action: 'Promote' | 'Demote',
): Promise<void> {
  await userAction(p, username, action).click();
  await userAction(p, username, 'Confirm').click();
  await userAction(p, username, action === 'Promote' ? 'Demote' : 'Promote').waitFor();
}
