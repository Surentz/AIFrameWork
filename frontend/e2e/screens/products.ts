import type { Locator, Page } from '@playwright/test';

export const pageHeading = (p: Page): Locator => p.getByRole('heading', { name: 'Catalogue' });
export const skuField = (p: Page): Locator => p.getByLabel('Sku');
export const nameField = (p: Page): Locator => p.getByLabel('Name');
export const descriptionField = (p: Page): Locator => p.getByLabel('Description');
export const priceField = (p: Page): Locator => p.getByLabel('Price');
export const createButton = (p: Page): Locator => p.getByRole('button', { name: 'Add product' });
export const saveButton = (p: Page): Locator => p.getByRole('button', { name: 'Save changes' });
export const editLink = (p: Page): Locator => p.getByRole('link', { name: 'Edit' });

/** A row's link in the list, which carries the product NAME rather than its sku. */
export const productLink = (p: Page, name: string): Locator =>
  p.getByRole('link', { name, exact: true });

/** The detail page's <h1>, which is the product name. */
export const detailHeading = (p: Page, name: string): Locator =>
  p.getByRole('heading', { name, exact: true });

/**
 * The <dd> paired with a <dt> in the detail page's fact list, whose markup is
 * <div><dt>Price</dt><dd>9.99</dd></div>. There is no role that distinguishes one pair from
 * another, so this pairs them structurally rather than asserting on document order.
 */
export const fact = (p: Page, term: string): Locator =>
  p
    .locator('.product-facts > div')
    .filter({ has: p.getByText(term, { exact: true }) })
    .locator('dd');

export interface NewProductForm {
  readonly sku: string;
  readonly name: string;
  readonly description?: string;
  readonly price: string;
}

export async function createProduct(p: Page, product: NewProductForm): Promise<void> {
  await p.goto('/products/new');
  await skuField(p).fill(product.sku);
  await nameField(p).fill(product.name);
  if (product.description !== undefined) {
    await descriptionField(p).fill(product.description);
  }

  await priceField(p).fill(product.price);
  await createButton(p).click();
}

export async function editProduct(
  p: Page,
  id: string,
  changes: { name: string; price: string },
): Promise<void> {
  await p.goto(`/products/${id}/edit`);
  await nameField(p).fill(changes.name);
  await priceField(p).fill(changes.price);
  await saveButton(p).click();
}
