import { expect, test } from '../../fixtures/index.ts';
import * as login from '../../screens/login.ts';

// @local-only: five failed attempts plus the real one are six auth-endpoint permits, more than
// half the cluster's shared 10-per-60-seconds budget on their own.
//
// freshUser, never workerUser: the fifth failure locks the account AND rotates its security
// stamp, which would sign out every later test on the worker.
test(
  'refuses even the right password once the account is locked, without saying so',
  { tag: '@local-only' },
  async ({ page, api, freshUser }) => {
    for (let attempt = 0; attempt < 5; attempt += 1) {
      await api.failSignIn(freshUser.username);
    }

    await login.signIn(page, freshUser);

    // The SAME message as a wrong password - one error for every failure mode. A distinct "too
    // many attempts" would let anyone probe which usernames exist (see SignIn.Failed).
    await expect(login.alert(page)).toHaveText(/do not match/i);
    await expect(page).toHaveURL(/\/login$/);
  },
);
