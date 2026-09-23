import { expect, test } from '../../fixtures/index.ts';
import * as notifications from '../../screens/notifications.ts';
import * as orders from '../../screens/orders.ts';
import * as products from '../../screens/products.ts';
import { uniqueSku } from '../../support/identity.ts';

/**
 * The feed end to end: a command commits, the outbox pump runs the notifier on its own schedule,
 * and the row reaches the page over REST. Every arrange ends in `api.waitForNotification`, which
 * absorbs the pump's delay over HTTP so the page is asserted on its first render.
 *
 * Untagged: nothing here needs the cache off or an empty database, and the feed is never cached
 * (see the notifications skill), so it runs against the cluster too - where the pump that writes
 * a row can be on either replica.
 *
 * The notification BODY is what these specs locate rows by. It carries the unique sku, where the
 * title ("Order placed") is shared by every order the worker's user has ever placed.
 */

test('tells the buyer their order was placed, and links to it', async ({
  signedInPage,
  api,
  workerUser,
}) => {
  const sku = uniqueSku();
  await api.placeOrder(workerUser, { sku, quantity: 2 });
  await api.waitForNotification(workerUser, { kind: 'OrderPlaced', text: sku });

  await notifications.open(signedInPage);

  const row = notifications.item(signedInPage, sku);
  await expect(row).toContainText('Order placed');
  await expect(row).toContainText(`We have your order for 2 × ${sku}.`);

  await notifications.viewLink(signedInPage, sku).click();
  await expect(orders.detailHeading(signedInPage, sku)).toBeVisible();
});

test('tells the buyer when an order ships or is cancelled', async ({
  signedInPage,
  api,
  workerUser,
}) => {
  const shipped = uniqueSku();
  const cancelled = uniqueSku();
  const reason = 'Ordered the wrong size.';

  await test.step('arrange: one order shipped, one cancelled', async () => {
    const [shippedId, cancelledId] = await Promise.all([
      api.placeOrder(workerUser, { sku: shipped, quantity: 1 }),
      api.placeOrder(workerUser, { sku: cancelled, quantity: 1 }),
    ]);
    await api.shipOrder(workerUser, shippedId);
    await api.cancelOrder(workerUser, cancelledId, reason);

    await Promise.all([
      api.waitForNotification(workerUser, { kind: 'OrderShipped', text: shipped }),
      api.waitForNotification(workerUser, { kind: 'OrderCancelled', text: cancelled }),
    ]);
  });

  await notifications.open(signedInPage);

  // Each order also has its "Order placed" row, so these filter on the body, which differs.
  await expect(
    notifications.item(signedInPage, `Your order for ${shipped} is on its way.`),
  ).toContainText('Order shipped');

  // The buyer's own reason is carried into the notification, not just the fact of cancellation.
  await expect(
    notifications.item(signedInPage, `Your order for ${cancelled} was cancelled.`),
  ).toContainText(reason);
});

test('tells a past buyer when the price of what they ordered changes', async ({
  signedInPage,
  api,
  workerUser,
}) => {
  const sku = uniqueSku();
  const productId = await api.createProduct(workerUser, { sku, name: sku, price: '19.95' });
  // Ordering it is what makes this user a past purchaser - the only people
  // ProductPriceChangedNotifier writes to.
  await api.orderProduct(workerUser, { sku, quantity: 1 });

  await api.updateProduct(workerUser, productId, { name: sku, price: '9.95' });
  await api.waitForNotification(workerUser, { kind: 'ProductPriceChanged', text: sku });

  await notifications.open(signedInPage);

  // The body is written once, InvariantCulture, and read back forever - so, unlike the product
  // page's price, its decimal separator is fixed.
  const text = `${sku} dropped from 19.95 to 9.95.`;
  await expect(notifications.item(signedInPage, text)).toContainText('Price changed');

  await notifications.viewLink(signedInPage, text).click();
  await expect(products.detailHeading(signedInPage, sku)).toBeVisible();
  await expect(products.fact(signedInPage, 'Price')).toHaveText(/9[.,]95/);
});

test('marks one notification read, and hides it from the unread view', async ({
  signedInPage,
  api,
  workerUser,
}) => {
  const sku = uniqueSku();
  await api.placeOrder(workerUser, { sku, quantity: 1 });
  await api.waitForNotification(workerUser, { kind: 'OrderPlaced', text: sku });

  await notifications.open(signedInPage);
  await notifications.unreadOnlyToggle(signedInPage).click();
  await expect(notifications.item(signedInPage, sku)).toBeVisible();

  await notifications.markReadButton(signedInPage, sku).click();

  // The unread view re-fetches after the mutation, and a read row no longer belongs in it.
  await expect(notifications.item(signedInPage, sku)).toBeHidden();

  await notifications.showAllToggle(signedInPage).click();
  await expect(notifications.item(signedInPage, sku)).toBeVisible();
  await expect(notifications.markReadButton(signedInPage, sku)).toBeHidden();
});

// freshUser, not workerUser: this asserts on the EXACT unread count and on an empty feed, and
// the worker's user accumulates notifications from every other test in this file.
test('counts unread notifications on the bell, and clears them all at once', async ({
  isolatedPage,
  api,
  freshUser,
}) => {
  await test.step('a new account starts with an empty feed', async () => {
    await notifications.open(isolatedPage);
    await expect(notifications.emptyState(isolatedPage, 'No notifications yet.')).toBeVisible();
    await expect(notifications.bell(isolatedPage)).toHaveAccessibleName(
      'Notifications, none unread',
    );
  });

  await test.step('two orders put two unread rows on the bell', async () => {
    const skus = [uniqueSku(), uniqueSku()];
    for (const sku of skus) {
      await api.placeOrder(freshUser, { sku, quantity: 1 });
    }
    await Promise.all(
      skus.map((sku) => api.waitForNotification(freshUser, { kind: 'OrderPlaced', text: sku })),
    );

    // The badge polls every thirty seconds; a navigation asks straight away.
    await notifications.open(isolatedPage);
    await expect(notifications.bell(isolatedPage)).toHaveAccessibleName('Notifications, 2 unread');
  });

  await test.step('mark all read empties the unread view and the bell', async () => {
    await notifications.markAllReadButton(isolatedPage).click();
    await expect(notifications.bell(isolatedPage)).toHaveAccessibleName(
      'Notifications, none unread',
    );

    await notifications.unreadOnlyToggle(isolatedPage).click();
    await expect(notifications.emptyState(isolatedPage, 'Nothing unread.')).toBeVisible();
  });
});
