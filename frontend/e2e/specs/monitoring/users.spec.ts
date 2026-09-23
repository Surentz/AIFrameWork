import { expect, test } from '../../fixtures/index.ts';
import * as monitoring from '../../screens/monitoring.ts';
import { ADMIN_USERNAME } from '../../support/identity.ts';

/**
 * Administering an account end to end: a real cookie, the real policy, and the real rails.
 *
 * Tagged @local-only for the reason every administrator spec here is — the role comes from
 * Admin__Usernames, which only the stack playwright.config.ts starts sets.
 */
test.describe('user management', { tag: '@local-only' }, () => {
  test('promotes and then demotes another account', async ({ adminPage, api }) => {
    const subject = await api.register();
    const row = monitoring.userRow(adminPage, subject.username);

    await test.step('find the account', async () => {
      await adminPage.goto('/monitoring/users');
      await monitoring.findUser(adminPage, subject.username);
      await expect(row).toContainText('Member');
    });

    await test.step('promote it', async () => {
      await monitoring.userAction(adminPage, subject.username, 'Promote').click();
      // Confirmation names the account in words, so a misclick cannot act on the wrong person.
      await expect(
        monitoring.confirmation(adminPage, `Promote ${subject.username} to Admin?`),
      ).toBeVisible();
      await monitoring.userAction(adminPage, subject.username, 'Confirm').click();

      await expect(row).toContainText('Admin');
    });

    await test.step('demote it again', async () => {
      await monitoring.userAction(adminPage, subject.username, 'Demote').click();
      await monitoring.userAction(adminPage, subject.username, 'Confirm').click();

      await expect(row).toContainText('Member');
    });
  });

  test('records every change in that account\'s history', async ({ adminPage, api }) => {
    const subject = await api.register();

    await adminPage.goto('/monitoring/users');
    await monitoring.findUser(adminPage, subject.username);
    await monitoring.changeRole(adminPage, subject.username, 'Promote');

    await monitoring.userAction(adminPage, subject.username, 'History').click();

    // The audit is the point of the feature, not decoration: who did what to whom.
    await expect(monitoring.userHistory(adminPage, subject.username)).toContainText('Promoted');
  });

  test('offers the acting administrator no actions on their own row', async ({ adminPage }) => {
    await adminPage.goto('/monitoring/users');

    // The server refuses both regardless; this is the explanation rather than the mechanism.
    const me = monitoring.userRow(adminPage, ADMIN_USERNAME);
    await expect(me).toContainText('You');
    await expect(monitoring.userAction(adminPage, ADMIN_USERNAME, 'Demote')).toHaveCount(0);
  });
});
