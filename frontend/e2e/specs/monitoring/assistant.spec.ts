import { expect, test } from '../../fixtures/index.ts';
import * as assistant from '../../screens/assistant.ts';

/**
 * The ops assistant end to end: the real API, its tool reading the real job-health query, and the
 * fake Messages API in Anthropic's place (tests/PartnerSimulator `messages`, ADR 0033).
 */
// @local-only: needs the administrator role and the fake, which only the managed stack starts.
test.describe('assistant', { tag: '@local-only' }, () => {
  test('answers a question from the overview, showing the tool it used', async ({ adminPage }) => {
    await adminPage.goto('/monitoring');
    await assistant.overviewLink(adminPage).click();
    await expect(assistant.heading(adminPage)).toBeVisible();

    await assistant.question(adminPage).fill('Did any background job fail today?');
    await assistant.askButton(adminPage).click();

    await expect(assistant.toolsUsed(adminPage)).toContainText('Checked job health');
    await expect(assistant.conversation(adminPage)).toContainText(
      'No background job failed in the last 24 hours.',
    );
    await expect(assistant.askButton(adminPage)).toBeVisible();
  });
});
