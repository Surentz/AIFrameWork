import type { Locator, Page } from '@playwright/test';

export const pageHeading = (p: Page): Locator =>
  p.getByRole('heading', { name: 'Fulfilment', level: 1 });

/** The shell's nav entry, rendered only for an administrator. */
export const navLink = (p: Page): Locator => p.getByRole('link', { name: 'Fulfilment' });

/** The refusal `RequireRole` renders in place of the page. */
export const refusal = (p: Page): Locator => p.getByRole('alert');

const loadMoreButton = (p: Page): Locator => p.getByRole('button', { name: 'Load more' });

/** One order's row, found by its sku — `api.placeOrder` names the product after it. */
export const orderRow = (p: Page, sku: string): Locator =>
  p.getByRole('table', { name: 'Orders waiting to ship' }).locator('tbody tr').filter({
    hasText: sku,
  });

/** A button in one order's row: 'Ship', then 'Confirm' or 'Cancel'. */
export const rowAction = (p: Page, sku: string, name: string): Locator =>
  orderRow(p, sku).getByRole('button', { name, exact: name !== 'Ship' });

/**
 * Pages through the queue until `sku`'s row is on screen. The queue is oldest first and holds
 * every order the run has placed, so an order placed a moment ago is on the LAST page.
 */
export async function findOrder(p: Page, sku: string): Promise<void> {
  await pageHeading(p).waitFor();
  while (!(await orderRow(p, sku).isVisible())) {
    const more = loadMoreButton(p);
    if (!(await more.isVisible())) {
      return;
    }
    await more.click();
    // The button reads "Loading more…" while the page is in flight. Waiting it out means the
    // next check sees the appended rows; a click that lands while it is disabled simply waits.
    await p.getByRole('button', { name: 'Loading more…' }).waitFor({ state: 'detached' });
  }
}
