import { expect, test } from '../../fixtures/index.ts';
import * as products from '../../screens/products.ts';
import { uniqueProductSku } from '../../support/identity.ts';

// Deliberately untagged, so these also run against the kind cluster, where Cache__Enabled is
// 'true' and there are two API replicas. That makes the first test an end-to-end check of
// ADR 0010's claim that ingress cookie affinity keeps L1 eviction correct - the write and the
// read have to land on the same pod for the new product to appear. It matters more here than
// for orders: CreateProduct's cache tags are scoped to the CALLING user, so this is the only
// eviction that is expected to reach anything at all.
//
// The forms are the operator's (ADR 0025), so the tests that drive them run as `adminPage`, which
// the kind overlay names too; reading the catalogue stays a member's, as in the paging test.
test('refuses the add-a-product form to an ordinary member', async ({ signedInPage }) => {
  await signedInPage.goto('/products/new');

  await expect(products.refusal(signedInPage)).toContainText('This page is for administrators');
});

test('adds a product and sees it in the catalogue', async ({ adminPage }) => {
  const sku = uniqueProductSku();

  await products.createProduct(adminPage, {
    sku,
    name: sku,
    description: 'Added by an end-to-end test.',
    price: '12.50',
  });

  // The detail heading is the NAME, which this test sets to the sku so it is unique.
  await expect(products.detailHeading(adminPage, sku)).toBeVisible();
  // A regex, not '12.50': the price is rendered through toLocaleString, so the decimal
  // separator follows the browser's locale and playwright.config.ts pins none. What this
  // asserts is the part that is ours - two decimal places, always.
  await expect(products.fact(adminPage, 'Price')).toHaveText(/12[.,]50/);

  await adminPage.goto('/products');
  // "Contains", never "equals": the catalogue is global and accumulates across tests and runs -
  // and "on page one" is an equality in disguise, since a parallel worker's products can land
  // above this one. The read still proves the eviction: it is this caller's own list.
  await products.findProduct(adminPage, sku);
  await expect(products.productLink(adminPage, sku)).toBeVisible();
});

test('edits a product without changing its sku', async ({ adminPage, api }) => {
  const sku = uniqueProductSku();
  const id = await api.createProduct({ sku, name: sku, price: '5.00' });
  const renamed = `${sku}-RENAMED`;

  await products.editProduct(adminPage, id, { name: renamed, price: '7.25' });

  await expect(products.detailHeading(adminPage, renamed)).toBeVisible();
  await expect(products.fact(adminPage, 'Price')).toHaveText(/7[.,]25/);
  // The sku is set at creation and the domain refuses to change it.
  await expect(products.fact(adminPage, 'Sku')).toHaveText(sku);
});

test('refuses a duplicate sku', async ({ adminPage, api }) => {
  const sku = uniqueProductSku();
  await api.createProduct({ sku, name: sku, price: '1.00' });

  await products.createProduct(adminPage, { sku, name: `${sku}-SECOND`, price: '2.00' });

  await expect(adminPage.getByRole('alert')).toContainText('already in the catalogue');
});

test('opens the edit form from the product page, filled with what is there', async ({
  adminPage,
  api,
}) => {
  const sku = uniqueProductSku();
  const id = await api.createProduct({ sku, name: sku, price: '3.45' });

  await adminPage.goto(`/products/${id}`);
  await products.editLink(adminPage).click();

  await expect(adminPage).toHaveURL(new RegExp(`/products/${id}/edit$`));
  await expect(products.nameField(adminPage)).toHaveValue(sku);
  // Not a price with a trailing zero: the form seeds the field with String(price) from a JSON
  // number, so "3.40" comes back as "3.4" - the same value, but not the same text.
  await expect(products.priceField(adminPage)).toHaveValue('3.45');
});

test('explains invalid fields next to each one', async ({ adminPage }) => {
  // 1.005, not a negative: three decimal places is the case ProductFields.tsx keeps a text input
  // for. A type="number" field with step="0.01" would block the submit in the browser, and the
  // server's message - the one asserted here - would never arrive.
  await products.createProduct(adminPage, { sku: uniqueProductSku(), name: '', price: '1.005' });

  // Both messages come from the server's validator and are wired to their fields through
  // aria-describedby - the assertion is what a screen reader would announce.
  await expect(products.nameField(adminPage)).toHaveAttribute('aria-invalid', 'true');
  await expect(products.nameField(adminPage)).toHaveAccessibleDescription(/must not be empty/);
  await expect(products.priceField(adminPage)).toHaveAttribute('aria-invalid', 'true');
  await expect(products.priceField(adminPage)).toHaveAccessibleDescription(
    /cannot have more than 2 decimal places/,
  );
  await expect(adminPage).toHaveURL(/\/products\/new$/);
});

test('pages through the catalogue', async ({ signedInPage, api }) => {
  // The catalogue is global and only grows, so this guarantees MORE than a page rather than an
  // exact count: 21 of this test's own on top of whatever is already there.
  await Promise.all(
    Array.from({ length: 21 }, () => {
      const sku = uniqueProductSku();
      return api.createProduct({ sku, name: sku, price: '1.00' });
    }),
  );

  await signedInPage.goto('/products');
  await expect(products.listRows(signedInPage)).toHaveCount(20);

  await products.loadMoreButton(signedInPage).click();
  await expect
    .poll(() => products.listRows(signedInPage).count())
    .toBeGreaterThan(20);
});
