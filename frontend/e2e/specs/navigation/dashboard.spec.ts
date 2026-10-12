import { expect, test } from '../../fixtures/index.ts';
import * as dashboard from '../../screens/dashboard.ts';
import * as monitoring from '../../screens/monitoring.ts';
import * as orders from '../../screens/orders.ts';
import * as shell from '../../screens/shell.ts';

// Runs as workerUser / adminPage, so none of it spends an auth permit.

test('offers a member a tile for each of their groups, and no more', async ({ signedInPage }) => {
  await signedInPage.goto('/');

  await expect(dashboard.heading(signedInPage)).toBeVisible();
  for (const group of ['Sales', 'Catalogue', 'Insights', 'Account']) {
    await expect(dashboard.tile(signedInPage, group)).toBeVisible();
  }
  await expect(dashboard.tile(signedInPage, 'Operations')).toHaveCount(0);
  await expect(shell.groupButton(signedInPage, 'Operations')).toHaveCount(0);
});

test("reaches a page through its subtile, and opens that page's group", async ({
  signedInPage,
}) => {
  await signedInPage.goto('/');

  await dashboard.subtile(signedInPage, 'Sales', 'All orders').click();

  await expect(orders.pageHeading(signedInPage)).toBeVisible();
  await expect(shell.groupButton(signedInPage, 'Sales')).toHaveAttribute('aria-expanded', 'true');
});

test("opens the current page's group and folds and unfolds the others", async ({
  signedInPage,
}) => {
  await signedInPage.goto('/orders');

  await expect(shell.groupButton(signedInPage, 'Sales')).toHaveAttribute('aria-expanded', 'true');
  await expect(shell.groupButton(signedInPage, 'Catalogue')).toHaveAttribute(
    'aria-expanded',
    'false',
  );
  await expect(shell.navLink(signedInPage, 'Products')).toBeHidden();

  await shell.groupButton(signedInPage, 'Catalogue').click();

  await expect(shell.navLink(signedInPage, 'Products')).toBeVisible();
});

test.describe('for the operator', { tag: '@local-only' }, () => {
  test('adds the operations tile, and the sidebar reaches monitoring', async ({ adminPage }) => {
    await adminPage.goto('/');

    await expect(dashboard.tile(adminPage, 'Operations')).toBeVisible();

    await shell.goTo(adminPage, 'Operations', 'Monitoring');
    await expect(monitoring.overviewHeading(adminPage)).toBeVisible();
  });
});
