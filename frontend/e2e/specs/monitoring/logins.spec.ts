import { expect, test } from '../../fixtures/index.ts';
import * as monitoring from '../../screens/monitoring.ts';

/**
 * The sign-in audit (ADR 0022) from the operator's side. Every attempt is arranged over HTTP
 * against a user no other test touches, so the rows asserted on are this test's own.
 *
 * @local-only: the administrator role (see monitoring.spec.ts), and the lockout test alone spends
 * five auth-endpoint permits.
 */
test.describe('sign-ins', { tag: '@local-only' }, () => {
  test('records a failed attempt against the account it named', async ({
    adminPage,
    api,
    freshUser,
  }) => {
    await api.failSignIn(freshUser.username);

    await adminPage.goto('/monitoring/logins');
    await expect(monitoring.pageHeading(adminPage, 'Sign-ins')).toBeVisible();

    await monitoring.signInUsernameFilter(adminPage).fill(freshUser.username);
    await monitoring.signInOutcomeFilter(adminPage).selectOption('BadCredentials');

    await expect(monitoring.signInRows(adminPage, freshUser.username)).toHaveCount(1);
    await expect(monitoring.signInRows(adminPage, freshUser.username)).toContainText(
      'BadCredentials',
    );
  });

  test('lists an account locked out by repeated failures', async ({
    adminPage,
    api,
    freshUser,
  }) => {
    // User.MaxFailedSignInAttempts is 5, and the fifth locks the account. freshUser, never the
    // worker's user: a lockout also rotates the security stamp.
    for (let attempt = 0; attempt < 5; attempt += 1) {
      await api.failSignIn(freshUser.username);
    }

    await adminPage.goto('/monitoring/logins');

    await expect(
      monitoring.lockedAccountsTable(adminPage).getByRole('cell', {
        name: freshUser.username,
        exact: true,
      }),
    ).toBeVisible();
  });
});
