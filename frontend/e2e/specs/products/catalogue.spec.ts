import { expect, test } from '../../fixtures/index.ts';
import * as products from '../../screens/products.ts';
import { uniqueProductSku } from '../../support/identity.ts';

// Deliberately untagged, so these also run against the kind cluster, where Cache__Enabled is
// 'true' and there are two API replicas. That makes the first test an end-to-end check of
// ADR 0010's claim that ingress cookie affinity keeps L1 eviction correct - the write and the
// read have to land on the same pod for the new product to appear. It matters more here than
// for orders: CreateProduct's cache tags are scoped to the CALLING user, so this is the only
// eviction that is expected to reach anything at all.
test('adds a product and sees it in the catalogue', async ({ signedInPage }) => {
  const sku = uniqueProductSku();

  await products.createProduct(signedInPage, {
    sku,
    name: sku,
    description: 'Added by an end-to-end test.',
    price: '12.50',
  });

  // The detail heading is the NAME, which this test sets to the sku so it is unique.
  await expect(products.detailHeading(signedInPage, sku)).toBeVisible();
  // A regex, not '12.50': the price is rendered through toLocaleString, so the decimal
  // separator follows the browser's locale and playwright.config.ts pins none. What this
  // asserts is the part that is ours - two decimal places, always.
  await expect(products.fact(signedInPage, 'Price')).toHaveText(/12[.,]50/);

  await signedInPage.goto('/products');
  // "Contains", never "equals": the catalogue is global and accumulates across tests and runs.
  await expect(products.productLink(signedInPage, sku)).toBeVisible();
});

test('edits a product without changing its sku', async ({ signedInPage, api, workerUser }) => {
  const sku = uniqueProductSku();
  const id = await api.createProduct(workerUser, { sku, name: sku, price: '5.00' });
  const renamed = `${sku}-RENAMED`;

  await products.editProduct(signedInPage, id, { name: renamed, price: '7.25' });

  await expect(products.detailHeading(signedInPage, renamed)).toBeVisible();
  await expect(products.fact(signedInPage, 'Price')).toHaveText(/7[.,]25/);
  // The sku is set at creation and the domain refuses to change it.
  await expect(products.fact(signedInPage, 'Sku')).toHaveText(sku);
});

test('refuses a duplicate sku', async ({ signedInPage, api, workerUser }) => {
  const sku = uniqueProductSku();
  await api.createProduct(workerUser, { sku, name: sku, price: '1.00' });

  await products.createProduct(signedInPage, { sku, name: `${sku}-SECOND`, price: '2.00' });

  await expect(signedInPage.getByRole('alert')).toContainText('already in the catalogue');
});
