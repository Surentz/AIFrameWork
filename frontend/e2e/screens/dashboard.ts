import type { Locator, Page } from '@playwright/test';

export const heading = (p: Page): Locator =>
  p.getByRole('heading', { name: 'Dashboard', level: 1 });

/** A group's tile: a region named by its title. */
export const tile = (p: Page, group: string): Locator =>
  p.getByRole('region', { name: group, exact: true });

/** One page's subtile inside a tile. Named with its group, which keeps it apart from the sidebar's link. */
export const subtile = (p: Page, group: string, label: string): Locator =>
  tile(p, group).getByRole('link', { name: `${label}, in ${group}`, exact: true });
