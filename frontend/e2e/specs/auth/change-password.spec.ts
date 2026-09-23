import { expect, test } from '../../fixtures/index.ts';
import * as account from '../../screens/account.ts';
import * as login from '../../screens/login.ts';
import * as orders from '../../screens/orders.ts';
import * as shell from '../../screens/shell.ts';

// Tagged @local-only: each test here registers a fresh user and signs in again on top of that (4
// auth-endpoint calls total), and non-local runs share one limiter partition across the whole
// suite. Excluding this file keeps a cluster run comfortably inside its 10-per-60-seconds budget.

const NewPassword = 'a different long enough password';

// isolatedPage, not signedInPage: changing a password rotates User.SecurityStamp, which
// invalidates every cookie already issued for that user. Doing it to the worker's shared user
// would sign out every later test on this worker.
test(
  'changes the password and keeps the caller signed in',
  { tag: '@local-only' },
  async ({ isolatedPage, freshUser }) => {
    await account.changePassword(isolatedPage, {
      current: freshUser.password,
      next: NewPassword,
    });

    await expect(account.successAlert(isolatedPage)).toHaveText(/has been changed/i);

    // The regression this test exists for. ChangePassword rotates the stamp, so without
    // AuthController re-issuing this caller's cookie, the very next request 401s and changing your
    // own password signs you out. Nothing below e2e catches it: it needs a real browser holding a
    // real cookie.
    await isolatedPage.goto('/orders');
    await expect(orders.pageHeading(isolatedPage)).toBeVisible();
  },
);

test(
  'accepts the new password and refuses the old one',
  { tag: '@local-only' },
  async ({ isolatedPage, freshUser }) => {
    await test.step('change the password', async () => {
      await account.changePassword(isolatedPage, {
        current: freshUser.password,
        next: NewPassword,
      });
      await expect(account.successAlert(isolatedPage)).toBeVisible();
    });

    await test.step('sign out', async () => {
      await isolatedPage.goto('/orders');
      await shell.signOut(isolatedPage);
    });

    await test.step('the old password is refused', async () => {
      await login.signIn(isolatedPage, {
        username: freshUser.username,
        password: freshUser.password,
      });
      await expect(login.alert(isolatedPage)).toHaveText(/do not match/i);
    });

    await test.step('the new password is accepted', async () => {
      await login.signIn(isolatedPage, { username: freshUser.username, password: NewPassword });
      await expect(orders.pageHeading(isolatedPage)).toBeVisible();
    });
  },
);

test('refuses a wrong current password', { tag: '@local-only' }, async ({
  isolatedPage,
}) => {
  await account.changePassword(isolatedPage, {
    current: 'not the current password',
    next: NewPassword,
  });

  await expect(account.alert(isolatedPage)).toHaveText(/current password is not correct/i);
  // A refused change must not have signed the caller out on the way.
  await isolatedPage.goto('/orders');
  await expect(orders.pageHeading(isolatedPage)).toBeVisible();
});

test('explains a new password that is too short, next to the field', { tag: '@local-only' }, async ({
  isolatedPage,
  freshUser,
}) => {
  await account.changePassword(isolatedPage, { current: freshUser.password, next: 'short' });

  await expect(account.newPasswordField(isolatedPage)).toHaveAttribute('aria-invalid', 'true');
  await expect(account.newPasswordField(isolatedPage)).toHaveAccessibleDescription(/12/);
});
