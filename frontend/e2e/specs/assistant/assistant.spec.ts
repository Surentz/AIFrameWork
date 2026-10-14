import { expect, test } from '../../fixtures/index.ts';
import * as assistant from '../../screens/assistant.ts';

/**
 * The assistant end to end: the real API and its tools, with the fake Messages API in Anthropic's
 * place (tests/PartnerSimulator `messages`, ADRs 0033 and 0034). The fake picks its tool from what
 * the question was offered, so these also prove the scope reached the request. Questions the router
 * cannot place, sent to the AI with Ask the AI.
 */
// @local-only: the fake runs only on the managed stack.
test.describe('assistant', { tag: '@local-only' }, () => {
  test('a member asks from the header and reopens the conversation later', async ({
    signedInPage,
  }) => {
    // Exactly the wording the evaluation set holds as unsure: anything appended could route it to an
    // instant answer, and then Ask the AI is never offered.
    const text = 'Write me a poem about the rain';

    await test.step('ask from the header icon', async () => {
      await signedInPage.goto('/orders');
      await assistant.headerIcon(signedInPage).click();
      await assistant.askAi(signedInPage, text);

      await expect(assistant.toolsUsed(signedInPage)).toContainText('Checked your orders');
      await expect(assistant.conversation(signedInPage)).toContainText('You have no open orders.');
      await expect(signedInPage).toHaveURL(/\/assistant\/[0-9a-f-]{36}$/);
    });

    await test.step('find it in the sidebar after a reload', async () => {
      await signedInPage.reload();

      // The worker user may hold older conversations with this title; the open one is the newest.
      await expect(assistant.sidebarEntry(signedInPage, text)).toHaveAttribute(
        'aria-current',
        'page',
      );
      await expect(assistant.conversation(signedInPage)).toContainText('You have no open orders.');
    });
  });

  test('an operator gets the monitoring tools', async ({ adminPage }) => {
    await adminPage.goto('/assistant');
    await assistant.askAi(adminPage, 'Write me a poem about the rain');

    await expect(assistant.toolsUsed(adminPage)).toContainText('Checked job health');
    await expect(assistant.conversation(adminPage)).toContainText(
      'No background job failed in the last 24 hours.',
    );
  });
});
