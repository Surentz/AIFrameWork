import type { Locator, Page } from '@playwright/test';

export const pageHeading = (p: Page): Locator =>
  p.getByRole('heading', { name: 'Notifications', level: 1 });

/**
 * The bell in the app chrome. Its accessible name carries the count - "Notifications, 3 unread",
 * "Notifications, none unread" - because the visible badge is aria-hidden, so the name is what a
 * spec asserts on.
 */
export const bell = (p: Page): Locator => p.getByRole('link', { name: /^Notifications/ });

/**
 * One notification, found by text that only it carries - in practice a unique sku in its body.
 * Titles are NOT unique ("Order placed" is every order), which is why nothing here keys on one.
 */
export const item = (p: Page, text: string): Locator =>
  p.getByRole('listitem').filter({ hasText: text });

/** Present only while the row is unread; its accessible name is "Mark <title> read". */
export const markReadButton = (p: Page, text: string): Locator =>
  item(p, text).getByRole('button', { name: /^Mark .* read$/ });

/** The row's link to its subject, named "View <title>". */
export const viewLink = (p: Page, text: string): Locator =>
  item(p, text).getByRole('link', { name: /^View / });

export const unreadOnlyToggle = (p: Page): Locator =>
  p.getByRole('button', { name: 'Unread only' });
export const showAllToggle = (p: Page): Locator => p.getByRole('button', { name: 'Show all' });
export const markAllReadButton = (p: Page): Locator =>
  p.getByRole('button', { name: 'Mark all read' });

/** The empty state: "No notifications yet." or, filtered, "Nothing unread." */
export const emptyState = (p: Page, text: 'No notifications yet.' | 'Nothing unread.'): Locator =>
  p.getByText(text, { exact: true });

export async function open(p: Page): Promise<void> {
  await p.goto('/notifications');
  await pageHeading(p).waitFor();
}
