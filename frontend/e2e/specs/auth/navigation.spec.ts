import { expect, test } from '../../fixtures/index.ts';
import * as login from '../../screens/login.ts';
import * as orders from '../../screens/orders.ts';
import * as register from '../../screens/register.ts';

// None of these submits a form, so none spends an auth rate-limit permit - they run everywhere.

test('sends a signed-in visitor from the root to their orders', async ({ signedInPage }) => {
  await signedInPage.goto('/');

  await expect(signedInPage).toHaveURL(/\/orders$/);
  await expect(orders.pageHeading(signedInPage)).toBeVisible();
});

test('links the sign-in and registration pages to each other', async ({ page }) => {
  await page.goto('/login');
  await login.registerLink(page).click();
  await expect(register.heading(page)).toBeVisible();

  await register.signInLink(page).click();
  await expect(login.heading(page)).toBeVisible();
});

test('reveals and hides the password as typed', async ({ page }) => {
  await page.goto('/login');
  await login.passwordField(page).fill('something secret');

  await expect(login.passwordField(page)).toHaveAttribute('type', 'password');

  await login.showPasswordButton(page).click();
  await expect(login.passwordField(page)).toHaveAttribute('type', 'text');
  // The value survives the switch; a remount would clear what the user already typed.
  await expect(login.passwordField(page)).toHaveValue('something secret');

  await login.hidePasswordButton(page).click();
  await expect(login.passwordField(page)).toHaveAttribute('type', 'password');
});
