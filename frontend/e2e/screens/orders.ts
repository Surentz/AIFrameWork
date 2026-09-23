import type { Locator, Page } from '@playwright/test';

export const pageHeading = (p: Page): Locator => p.getByRole('heading', { name: 'Orders' });
export const productField = (p: Page): Locator => p.getByLabel('Product');
export const quantityField = (p: Page): Locator => p.getByLabel('Quantity');
export const submitButton = (p: Page): Locator => p.getByRole('button', { name: 'Place order' });

/** The list's body rows - the header row excluded, which `getByRole('row')` would count. */
export const listRows = (p: Page): Locator =>
  p.getByRole('table', { name: 'Orders' }).locator('tbody tr');
export const loadMoreButton = (p: Page): Locator => p.getByRole('button', { name: 'Load more' });
export const emptyState = (p: Page): Locator => p.getByText('No orders yet.', { exact: true });
/** The empty state's call to action. */
export const placeFirstOrderLink = (p: Page): Locator =>
  p.getByRole('link', { name: 'Place the first one' });

/** A row's link in the list. */
export const orderLink = (p: Page, name: string): Locator => p.getByRole('link', { name });

/** The detail page's <h1> - the product name when the order carries a catalogue snapshot
    (every order placed since Task 4), the bare sku for a legacy order that predates it. */
export const detailHeading = (p: Page, name: string): Locator => p.getByRole('heading', { name });

/**
 * The <dd> paired with a <dt> in the detail page's fact list, whose markup is
 * <div><dt>Quantity</dt><dd>3</dd></div>. There is no role that distinguishes one pair from
 * another, so this pairs them structurally rather than asserting on document order.
 */
export const fact = (p: Page, term: string): Locator =>
  p
    .locator('.order-facts > div')
    .filter({ has: p.getByText(term, { exact: true }) })
    .locator('dd');

/** The detail page's sku, which links to the catalogue product the order was placed against. */
export const productLink = (p: Page, sku: string): Locator =>
  p.getByRole('link', { name: sku, exact: true });

export async function placeOrder(p: Page, order: { sku: string; quantity: number }): Promise<void> {
  await p.goto('/orders/new');
  await productField(p).selectOption(order.sku);
  await quantityField(p).fill(String(order.quantity));
  await submitButton(p).click();
}
