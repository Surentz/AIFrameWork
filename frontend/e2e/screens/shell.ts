import type { Locator, Page } from '@playwright/test';

// exact: true matters. Playwright matches an accessible name as a substring by default, and
// /account/password also has a "Sign out everywhere" button - without this, the locator resolves
// to two elements there and fails on strict mode.
export const signOutButton = (p: Page): Locator =>
  p.getByRole('button', { name: 'Sign out', exact: true });

export async function signOut(p: Page): Promise<void> {
  await signOutButton(p).click();
  await p.waitForURL('**/login');
}
