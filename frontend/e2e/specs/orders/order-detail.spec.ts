import { expect, test } from '../../fixtures/index.ts';
import * as orders from '../../screens/orders.ts';
import { uniqueSku } from '../../support/identity.ts';

// Arranged over HTTP rather than through the form: this test is about the detail page, and
// driving the place-order form to get there would make it fail for reasons that belong to
// place-order.spec.ts.
test('opens an order from the list', async ({ signedInPage, api, workerUser }) => {
  const sku = uniqueSku();
  await api.placeOrder(workerUser, { sku, quantity: 7 });

  await signedInPage.goto('/orders');
  await orders.orderLink(signedInPage, sku).click();

  await expect(orders.detailHeading(signedInPage, sku)).toBeVisible();
  await expect(orders.fact(signedInPage, 'Quantity')).toHaveText('7');
});
