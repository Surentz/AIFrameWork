import type { Locator, Page } from '@playwright/test';
import { PASSWORD } from '../support/identity.ts';

export const usernameField = (p: Page): Locator => p.getByLabel('Username');
export const displayNameField = (p: Page): Locator => p.getByLabel('Display name');
export const passwordField = (p: Page): Locator => p.getByLabel('Password', { exact: true });
export const submitButton = (p: Page): Locator => p.getByRole('button', { name: 'Create account' });
export const alert = (p: Page): Locator => p.getByRole('alert');

/** Fills and submits the form. Does not wait for navigation - the caller decides what success means. */
export async function submit(
  p: Page,
  account: { username: string; password?: string; displayName?: string },
): Promise<void> {
  await p.goto('/register');
  await usernameField(p).fill(account.username);
  await displayNameField(p).fill(account.displayName ?? 'E2E Tester');
  await passwordField(p).fill(account.password ?? PASSWORD);
  await submitButton(p).click();
}
