import type { Locator, Page } from '@playwright/test';

export const pageHeading = (p: Page): Locator => p.getByRole('heading', { name: 'Exports' });

/** The page's one button, as it reads when no export is being built. */
export const exportButton = (p: Page): Locator =>
  p.getByRole('button', { name: 'Export my orders' });

/** The newest Ready export's download - the list is newest first, and each link's name starts
    "Download export requested …", which a substring match on "Download" finds. */
export const downloadLink = (p: Page): Locator =>
  p.getByRole('table', { name: 'Exports' }).getByRole('link', { name: 'Download' }).first();

/** The header's nav entry for this page. */
export const navLink = (p: Page): Locator => p.getByRole('link', { name: 'Exports', exact: true });
