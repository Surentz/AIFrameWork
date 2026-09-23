import { expect, test } from '../../fixtures/index.ts';
import * as orders from '../../screens/orders.ts';
import * as register from '../../screens/register.ts';
import { uniqueUsername } from '../../support/identity.ts';

// Tagged @local-only: both tests here spend auth-endpoint rate-limit permits beyond the usual one
// per worker (UI register, api.register, and the duplicate attempt - 3 total), and non-local runs
// share one limiter partition across the whole suite. Excluding this file keeps a cluster run
// comfortably inside its 10-per-60-seconds budget.

// The only test that drives the registration form. Every other test registers over HTTP, so
// without this one the form would have no coverage at all.
test('registers a new account through the form', { tag: '@local-only' }, async ({ page }) => {
  await register.submit(page, { username: uniqueUsername() });

  // Registration signs in and lands on /orders; waiting for that is what makes this a real
  // session rather than a race.
  await page.waitForURL('**/orders');
  await expect(orders.pageHeading(page)).toBeVisible();
});

test('refuses a username that is already taken', { tag: '@local-only' }, async ({ page, api }) => {
  const existing = await api.register();

  await register.submit(page, { username: existing.username });

  // RegisterUserHandler answers 409 with "The username '<name>' is already taken."; the SPA puts
  // a ProblemDetails message with no field errors into the page-level alert.
  await expect(register.alert(page)).toHaveText(/already taken/i);
  await expect(page).toHaveURL(/\/register$/);
});

test('explains a password that is too short, next to the field', { tag: '@local-only' }, async ({
  page,
}) => {
  await register.submit(page, { username: uniqueUsername(), password: 'too short' });

  // PasswordPolicy.MinimumLength is 12. The message is wired to the field through
  // aria-describedby, which is what a screen reader reads out on focus - so that is the assertion.
  await expect(register.passwordField(page)).toHaveAttribute('aria-invalid', 'true');
  await expect(register.passwordField(page)).toHaveAccessibleDescription(/12/);
  await expect(page).toHaveURL(/\/register$/);
});

test('refuses a username with characters outside the allowed set', { tag: '@local-only' }, async ({
  page,
}) => {
  await register.submit(page, { username: 'has spaces' });

  await expect(register.usernameField(page)).toHaveAccessibleDescription(
    /letters, digits, dots, underscores and hyphens/,
  );
});
