import { expect, test } from '../../fixtures/index.ts';
import * as orders from '../../screens/orders.ts';
import * as products from '../../screens/products.ts';
import { uniqueSku } from '../../support/identity.ts';

// Arranged over HTTP rather than through the form: these tests are about the detail page, and
// driving the place-order form to get there would make them fail for reasons that belong to
// place-order.spec.ts.
test('opens an order from the list', async ({ signedInPage, api, workerUser }) => {
  const sku = uniqueSku();
  await api.placeOrder(workerUser, { sku, quantity: 7 });

  await signedInPage.goto('/orders');
  await orders.orderLink(signedInPage, sku).click();

  await expect(orders.detailHeading(signedInPage, sku)).toBeVisible();
  await expect(orders.fact(signedInPage, 'Quantity')).toHaveText('7');
});

test('prices the order from its catalogue snapshot, and links back to the product', async ({
  signedInPage,
  api,
  workerUser,
}) => {
  const sku = uniqueSku();
  // api.placeOrder prices the product it creates at 19.95.
  const id = await api.placeOrder(workerUser, { sku, quantity: 3 });

  await signedInPage.goto(`/orders/${id}`);

  // Regexes for the separator only: prices render through toLocaleString and the config pins no
  // locale. The arithmetic - 3 × 19.95 - is what is under test.
  await expect(orders.fact(signedInPage, 'Unit price')).toHaveText(/^19[.,]95$/);
  await expect(orders.fact(signedInPage, 'Total')).toHaveText(/^59[.,]85$/);

  await orders.productLink(signedInPage, sku).click();
  await expect(products.detailHeading(signedInPage, sku)).toBeVisible();
});
