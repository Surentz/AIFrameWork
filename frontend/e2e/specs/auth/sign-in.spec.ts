import { expect, test } from '../../fixtures/index.ts';
import * as login from '../../screens/login.ts';
import * as orders from '../../screens/orders.ts';
import * as shell from '../../screens/shell.ts';

/** `options.Cookie.Name` in src/Api/Program.cs. */
const SessionCookie = 'aiframework.session';

test('signs out and back in', async ({ signedInPage, workerUser }) => {
  await signedInPage.goto('/orders');
  await shell.signOut(signedInPage);

  await login.usernameField(signedInPage).fill(workerUser.username);
  await login.passwordField(signedInPage).fill(workerUser.password);
  await login.submitButton(signedInPage).click();

  await expect(orders.pageHeading(signedInPage)).toBeVisible();

  // "Remember me" was left unticked, so the cookie must end with the browser: a session cookie,
  // which Playwright reports with an expiry of -1.
  const [session] = await signedInPage.context().cookies();
  expect(session?.name).toBe(SessionCookie);
  expect(session?.expires).toBe(-1);
});

// @local-only for the auth budget alone: one more sign-in, and a kind run's untagged tests already
// spend seven of the cluster's ten permits a minute (see e2e/CLAUDE.md). What it checks - the
// cookie's expiry - does not vary by target.
test('keeps a remembered session beyond the browser', { tag: '@local-only' }, async ({
  page,
  workerUser,
}) => {
  await login.signIn(page, { ...workerUser, rememberMe: true });
  await expect(orders.pageHeading(page)).toBeVisible();

  // IsPersistent is the whole of what "remember me" means under cookie authentication: the
  // cookie carries an expiry, so it outlives the browser session.
  const session = (await page.context().cookies()).find((c) => c.name === SessionCookie);
  expect(session?.expires).toBeGreaterThan(Date.now() / 1000 + 60 * 60);
});

test('sends an anonymous visitor to the login page', async ({ page }) => {
  await page.goto('/orders');

  // The guard is a convenience; the real refusal is [Authorize] answering 401. This proves the
  // two agree - the visitor lands somewhere useful rather than on a page full of failed requests.
  await expect(page).toHaveURL(/\/login$/);
  await expect(login.heading(page)).toBeVisible();
});

test('keeps the session across a reload', async ({ signedInPage }) => {
  await signedInPage.goto('/orders');
  await signedInPage.reload();

  // The cookie is what survives here; nothing is kept in memory or localStorage.
  await expect(orders.pageHeading(signedInPage)).toBeVisible();
});

// One deliberate failed sign-in against the shared worker user. User.MaxFailedSignInAttempts is
// 5, and the fifth locks the account AND rotates the security stamp - which would sign out every
// other test on this worker. One failure (two, if CI retries this test) is well inside that, but
// it is a budget: a second failed-login test must take `freshUser` rather than spend more of it.
test('refuses a wrong password without saying whether the account exists', async ({
  page,
  workerUser,
}) => {
  await login.signIn(page, { username: workerUser.username, password: 'not the right password' });

  await expect(login.alert(page)).toHaveText(/do not match/i);
});
