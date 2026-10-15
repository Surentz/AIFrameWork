import type { Locator, Page } from '@playwright/test';

export const missedQuestions = (p: Page): Locator => p.getByRole('table', { name: 'Missed questions' });
