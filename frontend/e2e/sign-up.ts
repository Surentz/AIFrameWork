import type { Page } from '@playwright/test';

/**
 * Registers a fresh account through the UI and leaves the page signed in.
 *
 * A new user per test rather than a seeded one: `global-teardown.ts` drops the database with
 * `down -v` on every run and nothing seeds rows, so the existing specs already generate their
 * own uniqueness inline (`SKU-E2E-${Date.now()}`). This follows that rather than adding a seed
 * step to `prepare-database.ts` or Playwright `storageState` machinery neither spec needs.
 */
export async function signUp(page: Page): Promise<string> {
  const username = `e2e${String(Date.now())}${String(Math.floor(Math.random() * 1000))}`;

  await page.goto('/register');
  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Display name').fill('E2E Tester');
  await page.getByLabel('Password').fill('a long enough e2e password');
  await page.getByRole('button', { name: 'Create account' }).click();

  // Registration signs in and lands on /orders; waiting for that is what makes the rest of the
  // test a real signed-in session rather than a race.
  await page.waitForURL('**/orders');

  return username;
}
