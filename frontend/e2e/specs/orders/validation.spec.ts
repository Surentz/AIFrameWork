import { expect, test } from '../../fixtures/index.ts';
import * as orders from '../../screens/orders.ts';

test('shows the server validation message for an invalid quantity', async ({ signedInPage }) => {
  await orders.placeOrder(signedInPage, { sku: 'SKU-E2E-INVALID', quantity: 0 });

  // Not /quantity/i - that matches the permanent <label>Quantity</label> and would pass whether
  // or not the server ever rejected anything. RuleFor(c => c.Quantity).GreaterThan(0) produces
  // "'Quantity' must be greater than '0'.", which appears only on failure.
  await expect(signedInPage.getByText(/must be greater than/i)).toBeVisible();
});
