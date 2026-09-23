import { expect, test } from '../../fixtures/index.ts';
import * as orders from '../../screens/orders.ts';

// freshUser, not workerUser: this asserts an EMPTY list and exact row counts, and the worker's
// user accumulates orders from every other spec. One registration, so it stays untagged.
test('starts empty, then pages through orders twenty at a time', async ({
  isolatedPage,
  api,
  freshUser,
}) => {
  await test.step('a new account has no orders, and is offered the form', async () => {
    await isolatedPage.goto('/orders');
    await expect(orders.emptyState(isolatedPage)).toBeVisible();

    await orders.placeFirstOrderLink(isolatedPage).click();
    await expect(isolatedPage).toHaveURL(/\/orders\/new$/);
  });

  // 25 is one full page of 20 (api/orders.ts's default limit) and a partial second one.
  await api.placeOrders(freshUser, 25);

  await test.step('the first page holds twenty', async () => {
    await isolatedPage.goto('/orders');
    await expect(orders.listRows(isolatedPage)).toHaveCount(20);
  });

  await test.step('"Load more" appends the rest, then goes away', async () => {
    await orders.loadMoreButton(isolatedPage).click();
    await expect(orders.listRows(isolatedPage)).toHaveCount(25);
    await expect(orders.loadMoreButton(isolatedPage)).toBeHidden();
  });
});
