import type { Locator, Page } from '@playwright/test';

export const overviewLink = (p: Page): Locator =>
  p.getByRole('link', { name: 'Ask the assistant about jobs, traffic and integrations' });

export const heading = (p: Page): Locator =>
  p.getByRole('heading', { name: 'Ask the assistant', level: 1 });

export const question = (p: Page): Locator => p.getByRole('textbox', { name: 'Your question' });

export const askButton = (p: Page): Locator => p.getByRole('button', { name: 'Ask' });

export const conversation = (p: Page): Locator => p.getByRole('log', { name: 'Conversation' });

export const toolsUsed = (p: Page): Locator =>
  conversation(p).getByRole('list', { name: 'Tools used' });
