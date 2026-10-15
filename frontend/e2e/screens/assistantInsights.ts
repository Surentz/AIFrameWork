import type { Locator, Page } from '@playwright/test';

export const missedQuestions = (p: Page): Locator => p.getByRole('table', { name: 'Missed questions' });

/** The rows of the missed-questions table that carry this question. */
export const missedQuestion = (p: Page, question: string): Locator =>
  missedQuestions(p).getByRole('row').filter({ hasText: question });
