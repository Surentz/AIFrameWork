import type { Locator, Page } from '@playwright/test';

export const overviewHeading = (p: Page): Locator =>
  p.getByRole('heading', { name: 'Monitoring', level: 1 });

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
