import { expect, test } from '../../fixtures/index.ts';
import * as account from '../../screens/account.ts';
import * as login from '../../screens/login.ts';
import * as orders from '../../screens/orders.ts';

// Two independent cookie jars for one user. There is no lower layer that can test this: it is
// the whole point of the endpoint that a session OTHER than the caller's stops working.
test('ends another browser session', async ({ openSession, freshUser }) => {
  const first = await openSession(freshUser);
  const second = await openSession(freshUser);

  await test.step('the second session starts out signed in', async () => {
    await second.goto('/orders');
    await expect(orders.pageHeading(second)).toBeVisible();
  });

  await test.step('the first session signs out everywhere', async () => {
    await first.goto('/account/password');

    // Waiting on the response rather than the click: the rotation has to have been committed
    // before the second session navigates, or the test races the request.
    await Promise.all([
      first.waitForResponse(
        (response) =>
          response.url().includes('/api/auth/sign-out-everywhere') && response.status() === 204,
      ),
      account.signOutEverywhereButton(first).click(),
    ]);
  });

  await test.step('the second session is sent to the login page', async () => {
    await second.reload();

    // The stamp rotated, so the cookie this session holds no longer validates and RequireAuth
    // sends it to the login page.
    await expect(second).toHaveURL(/\/login$/);
    await expect(login.heading(second)).toBeVisible();
  });
});
