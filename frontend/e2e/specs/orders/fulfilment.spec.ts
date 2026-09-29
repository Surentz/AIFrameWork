import { expect, test } from '../../fixtures/index.ts';
import * as fulfilment from '../../screens/fulfilment.ts';
import { uniqueSku } from '../../support/identity.ts';

/**
 * The operator's queue end to end: a buyer places an order, the operator finds it among every
 * buyer's orders and ships it, and the buyer hears about it. ADR 0024.
 *
 * The operator tests are @local-only for the reason every administrator spec is — the role comes
 * from Admin__Usernames, which only the stack playwright.config.ts starts sets. The refusal is
 * untagged, like monitoring.spec.ts's access tests: refusing a member is the security-relevant
 * half and needs no administrator, so it runs everywhere.
 */

test('refuses an ordinary member', async ({ signedInPage }) => {
  await signedInPage.goto('/fulfilment');

  await expect(fulfilment.refusal(signedInPage)).toContainText('This page is for administrators');
  await expect(fulfilment.navLink(signedInPage)).toHaveCount(0);
});

test.describe('the fulfilment queue', { tag: '@local-only' }, () => {
  test('ships another buyer\'s order, which then leaves the queue', async ({
    adminPage,
    api,
    workerUser,
  }) => {
    const sku = uniqueSku();
    await api.placeOrder(workerUser, { sku, quantity: 3 });

    await test.step('find the order in the queue', async () => {
      await adminPage.goto('/');
      await fulfilment.navLink(adminPage).click();
      await fulfilment.findOrder(adminPage, sku);
      await expect(fulfilment.orderRow(adminPage, sku)).toContainText(workerUser.username);
    });

    await test.step('ship it', async () => {
      await fulfilment.rowAction(adminPage, sku, 'Ship').click();
      // Named in words: shipping cannot be undone, so a misclick must not ship the wrong order.
      await expect(fulfilment.orderRow(adminPage, sku)).toContainText(
        `Ship 3 × ${sku} to ${workerUser.username}?`,
      );
      await fulfilment.rowAction(adminPage, sku, 'Confirm').click();

      await expect(fulfilment.orderRow(adminPage, sku)).toHaveCount(0);
    });

    await test.step('the buyer is told', async () => {
      await api.waitForNotification(workerUser, { kind: 'OrderShipped', text: sku });
    });
  });

  test('leaves the order waiting when the confirmation is cancelled', async ({
    adminPage,
    api,
    workerUser,
  }) => {
    const sku = uniqueSku();
    await api.placeOrder(workerUser, { sku, quantity: 1 });

    await adminPage.goto('/fulfilment');
    await fulfilment.findOrder(adminPage, sku);
    await fulfilment.rowAction(adminPage, sku, 'Ship').click();
    await fulfilment.rowAction(adminPage, sku, 'Cancel').click();

    await expect(fulfilment.rowAction(adminPage, sku, 'Ship')).toBeVisible();
  });
});
