import { expect, test } from '@playwright/test';

test('places an order and sees it in the list', async ({ page }) => {
  const sku = `SKU-E2E-${String(Date.now())}`;

  await page.goto('/orders/new');
  await page.getByLabel('Sku').fill(sku);
  await page.getByLabel('Quantity').fill('3');
  await page.getByRole('button', { name: 'Place order' }).click();

  await expect(page.getByRole('heading', { name: sku })).toBeVisible();

  await page.goto('/orders');
  await expect(page.getByRole('link', { name: sku })).toBeVisible();
});

test('shows the server validation message for an invalid quantity', async ({ page }) => {
  await page.goto('/orders/new');
  await page.getByLabel('Sku').fill('SKU-E2E-INVALID');
  await page.getByLabel('Quantity').fill('0');
  await page.getByRole('button', { name: 'Place order' }).click();

  // Not /quantity/i - that matches the permanent <label>Quantity</label> and would pass
  // whether or not the server ever rejected anything. RuleFor(c => c.Quantity).GreaterThan(0)
  // produces "'Quantity' must be greater than '0'.", which appears only on failure.
  await expect(page.getByText(/must be greater than/i)).toBeVisible();
});
