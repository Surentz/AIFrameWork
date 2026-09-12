import type { Locator, Page } from '@playwright/test';

export const currentPasswordField = (p: Page): Locator => p.getByLabel('Current password');
export const newPasswordField = (p: Page): Locator => p.getByLabel('New password');
export const changeButton = (p: Page): Locator =>
  p.getByRole('button', { name: 'Change password' });
export const successAlert = (p: Page): Locator => p.getByRole('status');
export const alert = (p: Page): Locator => p.getByRole('alert');
export const signOutEverywhereButton = (p: Page): Locator =>
  p.getByRole('button', { name: 'Sign out everywhere' });

export async function changePassword(
  p: Page,
  passwords: { current: string; next: string },
): Promise<void> {
  await p.goto('/account/password');
  await currentPasswordField(p).fill(passwords.current);
  await newPasswordField(p).fill(passwords.next);
  await changeButton(p).click();
}
