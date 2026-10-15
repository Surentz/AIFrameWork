import { expect, test } from '../../fixtures/index.ts';
import * as assistant from '../../screens/assistant.ts';
import * as insights from '../../screens/assistantInsights.ts';
import * as shell from '../../screens/shell.ts';

// @local-only: needs instant answers, which only the managed stack configures.
test.describe('assistant insights', { tag: '@local-only' }, () => {
  test("a member's missed question, rated down, reaches an administrator's insights", async ({ signedInPage, adminPage }) => {
    await test.step('a member asks something the assistant cannot answer at once, and rates the reply down', async () => {
      await signedInPage.goto('/assistant');
      await assistant.ask(signedInPage, 'Write me a poem about the rain');
      await assistant.notHelpful(signedInPage).click();
      await expect(assistant.notHelpful(signedInPage)).toHaveAttribute('aria-pressed', 'true');
    });

    await test.step('an administrator sees it among the missed questions', async () => {
      await adminPage.goto('/');
      await shell.goTo(adminPage, 'Operations', 'Assistant insights');
      await expect(insights.missedQuestion(adminPage, 'Write me a poem about the rain').filter({ hasText: '👎' })).toBeVisible();
    });
  });
});
