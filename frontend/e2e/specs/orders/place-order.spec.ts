import { expect, test } from '../../fixtures/index.ts';
import * as orders from '../../screens/orders.ts';
import { uniqueProductSku } from '../../support/identity.ts';

// Deliberately untagged, so it also runs against the kind cluster, where Cache__Enabled is
// 'true' and there are two API replicas. That makes it an end-to-end check of ADR 0010's claim
// that ingress cookie affinity keeps L1 eviction correct - the write and the read have to land
// on the same pod for the new order to appear.
test('places an order and sees it in the list', async ({ signedInPage, api, workerUser }) => {
  const sku = uniqueProductSku();
  const name = 'Widget';
  await api.createProduct(workerUser, { sku, name, price: '19.95' });

  await orders.placeOrder(signedInPage, { sku, quantity: 3 });
  // The heading is the product name once an order carries a catalogue snapshot - every order
  // this spec places does, since PlaceOrder now requires one. See screens/orders.ts.
  await expect(orders.detailHeading(signedInPage, name)).toBeVisible();

  await signedInPage.goto('/orders');
  await expect(orders.orderLink(signedInPage, name)).toBeVisible();
});
