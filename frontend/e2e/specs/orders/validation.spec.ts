import { expect, test } from '../../fixtures/index.ts';
import * as orders from '../../screens/orders.ts';
import { uniqueProductSku } from '../../support/identity.ts';

test('shows the server validation message for an invalid quantity', async ({
  signedInPage,
  api,
  workerUser,
}) => {
  // A real catalogue sku, so this test still fails on the QUANTITY. With a sku the catalogue
  // does not hold, the request would be refused for the sku instead and this test would pass
  // while asserting nothing about quantity.
  const sku = uniqueProductSku();
  await api.createProduct(workerUser, { sku, name: 'Widget', price: '9.99' });

  await orders.placeOrder(signedInPage, { sku, quantity: 0 });

  // Not /quantity/i - that matches the permanent <label>Quantity</label> and would pass whether
  // or not the server ever rejected anything. RuleFor(c => c.Quantity).GreaterThan(0) produces
  // "'Quantity' must be greater than '0'.", which appears only on failure.
  await expect(signedInPage.getByText(/must be greater than/i)).toBeVisible();
});
