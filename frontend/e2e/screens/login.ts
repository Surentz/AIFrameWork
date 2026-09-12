import type { Locator, Page } from '@playwright/test';

export const heading = (p: Page): Locator => p.getByRole('heading', { name: 'Welcome back' });
export const usernameField = (p: Page): Locator => p.getByLabel('Username');
// exact: true - a substring match would also hit any "... password" label on the page.
export const passwordField = (p: Page): Locator => p.getByLabel('Password', { exact: true });
export const submitButton = (p: Page): Locator => p.getByRole('button', { name: 'Sign in' });
export const alert = (p: Page): Locator => p.getByRole('alert');

export async function signIn(
  p: Page,
  credentials: { username: string; password: string },
): Promise<void> {
  await p.goto('/login');
  await usernameField(p).fill(credentials.username);
  await passwordField(p).fill(credentials.password);
  await submitButton(p).click();
}
