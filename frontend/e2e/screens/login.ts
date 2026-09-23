import type { Locator, Page } from '@playwright/test';

export const heading = (p: Page): Locator => p.getByRole('heading', { name: 'Welcome back' });
export const usernameField = (p: Page): Locator => p.getByLabel('Username');
// exact: true - a substring match would also hit any "... password" label on the page.
export const passwordField = (p: Page): Locator => p.getByLabel('Password', { exact: true });
export const submitButton = (p: Page): Locator => p.getByRole('button', { name: 'Sign in' });
export const alert = (p: Page): Locator => p.getByRole('alert');
export const rememberMe = (p: Page): Locator => p.getByLabel('Remember me');
/** Named "Show password" or "Hide password" depending on state, so each is its own locator. */
export const showPasswordButton = (p: Page): Locator =>
  p.getByRole('button', { name: 'Show password' });
export const hidePasswordButton = (p: Page): Locator =>
  p.getByRole('button', { name: 'Hide password' });
export const registerLink = (p: Page): Locator =>
  p.getByRole('link', { name: 'Create an account' });

export async function signIn(
  p: Page,
  credentials: { username: string; password: string; rememberMe?: boolean },
): Promise<void> {
  await p.goto('/login');
  await usernameField(p).fill(credentials.username);
  await passwordField(p).fill(credentials.password);
  if (credentials.rememberMe === true) {
    await rememberMe(p).check();
  }
  await submitButton(p).click();
}
