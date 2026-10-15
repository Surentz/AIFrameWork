import type { Locator, Page } from '@playwright/test';

export const headerIcon = (p: Page): Locator =>
  p.getByRole('link', { name: 'Assistant', exact: true });

export const question = (p: Page): Locator => p.getByRole('textbox', { name: 'Your question' });

export const send = (p: Page): Locator => p.getByRole('button', { name: 'Send', exact: true });

export const conversation = (p: Page): Locator => p.getByRole('log', { name: 'Conversation' });

export const toolsUsed = (p: Page): Locator =>
  conversation(p).getByRole('list', { name: 'Tools used' }).last();

export const sidebar = (p: Page): Locator => p.getByRole('navigation', { name: 'Conversations' });

/**
 * The newest conversation with this title. The sidebar lists the most recently active first, and a
 * title is the first question, which other tests for the same worker user may have asked too.
 */
export const sidebarEntry = (p: Page, title: string): Locator =>
  sidebar(p).getByRole('link', { name: title, exact: true }).first();

export async function ask(p: Page, text: string): Promise<void> {
  await question(p).fill(text);
  await send(p).click();
}

/** The last instant or help answer: an article named "Instant answer" or "Help answer". */
export const answerCard = (p: Page): Locator => conversation(p).getByRole('article').last();

export const answerLink = (p: Page, name: string): Locator =>
  answerCard(p).getByRole('link', { name: new RegExp(`^${name}`) });

export const suggestion = (p: Page, text: string): Locator =>
  conversation(p).getByRole('button', { name: text, exact: true });

export const askAiButton = (p: Page): Locator =>
  conversation(p)
    .getByRole('button', { name: /^Ask the AI instead/ })
    .last();

/** Asks a question the router cannot place, then sends it to the AI. */
export async function askAi(p: Page, text: string): Promise<void> {
  await ask(p, text);
  await askAiButton(p).click();
}

/** The 👎 on the newest answer. */
export const notHelpful = (p: Page): Locator =>
  conversation(p).getByRole('group', { name: 'Rate this answer' }).last().getByRole('button', { name: 'Not helpful' });
