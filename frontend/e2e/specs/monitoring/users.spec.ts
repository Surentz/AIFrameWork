import { expect, test } from '../../fixtures/index.ts';
import * as monitoring from '../../screens/monitoring.ts';

/**
 * Administering an account end to end: a real cookie, the real policy, and the real rails.
 *
 * Tagged @local-only for the reason every administrator spec here is — the role comes from
 * Admin__Usernames, which only the stack playwright.config.ts starts sets.
 */
test.describe('user management', { tag: '@local-only' }, () => {
  test('promotes and then demotes another account', async ({ adminPage, api }) => {
    const subject = await api.register();

    await adminPage.goto('/monitoring/users');
    await monitoring.findUser(adminPage, subject.username);

    const row = monitoring.userRow(adminPage, subject.username);
    await expect(row).toContainText('Member');

    // Confirmation names the account in words, so a misclick cannot act on the wrong person.
    await row.getByRole('button', { name: 'Promote' }).click();
    await expect(adminPage.getByText(`Promote ${subject.username} to Admin?`)).toBeVisible();
    await row.getByRole('button', { name: 'Confirm' }).click();

    await expect(row).toContainText('Admin');

    await row.getByRole('button', { name: 'Demote' }).click();
    await row.getByRole('button', { name: 'Confirm' }).click();

    await expect(row).toContainText('Member');
  });

  test('records every change in that account\'s history', async ({ adminPage, api }) => {
    const subject = await api.register();

    await adminPage.goto('/monitoring/users');
    await monitoring.findUser(adminPage, subject.username);

    const row = monitoring.userRow(adminPage, subject.username);
    await row.getByRole('button', { name: 'Promote' }).click();
    await row.getByRole('button', { name: 'Confirm' }).click();
    await expect(row).toContainText('Admin');

    await row.getByRole('button', { name: 'History' }).click();

    // The audit is the point of the feature, not decoration: who did what to whom.
    const history = adminPage.getByRole('table', {
      name: new RegExp(`What has been done to ${subject.username}`),
    });
    await expect(history).toContainText('Promoted');
  });

  test('offers the acting administrator no actions on their own row', async ({ adminPage }) => {
    await adminPage.goto('/monitoring/users');

    // The server refuses both regardless; this is the explanation rather than the mechanism.
    const me = monitoring.userRow(adminPage, 'e2e-admin');
    await expect(me).toContainText('You');
    await expect(me.getByRole('button', { name: 'Demote' })).toHaveCount(0);
  });
});
