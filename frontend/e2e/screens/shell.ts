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

/** The sidebar. Named "Main" so it is not confused with the assistant's "Conversations" navigation. */
export const mainNav = (p: Page): Locator => p.getByRole('navigation', { name: 'Main' });

/** A page's link in the sidebar. Not visible while its group is folded. */
export const navLink = (p: Page, label: string): Locator =>
  mainNav(p).getByRole('link', { name: label, exact: true });

/** A group's fold button. Absent, not just folded, for a group the user may not see. */
export const groupButton = (p: Page, group: string): Locator =>
  mainNav(p).getByRole('button', { name: group, exact: true });

/** Unfolds the group if it is folded, then follows the link. */
export async function goTo(p: Page, group: string, label: string): Promise<void> {
  const button = groupButton(p, group);
  if ((await button.getAttribute('aria-expanded')) !== 'true') {
    await button.click();
  }
  await navLink(p, label).click();
}
