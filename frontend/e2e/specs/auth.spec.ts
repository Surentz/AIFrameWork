import { expect, test } from '@playwright/test';
import { signUp } from '../support/sign-up.ts';

test('registers, signs out, and signs back in', async ({ page }) => {
  const username = await signUp(page);

  await page.getByRole('button', { name: 'Sign out' }).click();
  await page.waitForURL('**/login');

  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Password', { exact: true }).fill('a long enough e2e password');
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(page.getByRole('heading', { name: 'Orders' })).toBeVisible();
});

test('sends an anonymous visitor to the login page', async ({ page }) => {
  await page.goto('/orders');

  // The guard is a convenience; the real refusal is [Authorize] answering 401. This proves the
  // two agree - the visitor lands somewhere useful rather than on a page full of failed requests.
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
});

test('keeps the session across a reload', async ({ page }) => {
  await signUp(page);

  await page.reload();

  // The cookie is what survives here; nothing is kept in memory or localStorage.
  await expect(page.getByRole('heading', { name: 'Orders' })).toBeVisible();
});

test('refuses a wrong password without saying whether the account exists', async ({ page }) => {
  const username = await signUp(page);
  await page.getByRole('button', { name: 'Sign out' }).click();
  await page.waitForURL('**/login');

  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Password', { exact: true }).fill('not the right password');
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(page.getByRole('alert')).toHaveText(/do not match/i);
});
