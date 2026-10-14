import { expect, test } from '../../fixtures/index.ts';
import * as assistant from '../../screens/assistant.ts';

/**
 * Instant answers end to end (ADR 0036): the real API, the real local model and router, the real
 * caller-scoped queries. "Write me a poem about the rain" is labelled unsure in the evaluation set
 * (tests/Infrastructure.Tests/Assistant/Quick/evaluation.en.json), so CI guarantees it never gets an
 * instant answer.
 */
// @local-only: instant answers are configured on the managed stack, and the AI half needs the fake
// Messages API, which runs only there. Runs as workerUser: no auth permit is spent.
test.describe('instant answers', { tag: '@local-only' }, () => {
  test('a member asks about their last order and opens it from the answer', async ({ signedInPage, api, workerUser }) => {
    const [sku] = await api.placeOrders(workerUser, 1);
    if (sku === undefined) {
      throw new Error('placeOrders returned no sku');
    }

    await test.step('ask', async () => {
      await signedInPage.goto('/assistant');
      await assistant.ask(signedInPage, 'Has my last order shipped?');

      await expect(assistant.answerCard(signedInPage)).toContainText('Your last order has not shipped yet.');
      await expect(assistant.answerCard(signedInPage)).toContainText(sku);
    });

    await test.step('follow the link', async () => {
      await assistant.answerLink(signedInPage, 'Open order').click();

      await expect(signedInPage).toHaveURL(/\/orders\/[0-9a-f-]{36}$/);
    });
  });

  test('a question it cannot place offers suggestions, and the AI answers on request', async ({ signedInPage }) => {
    await signedInPage.goto('/assistant');
    await assistant.ask(signedInPage, 'Write me a poem about the rain');

    await expect(assistant.conversation(signedInPage)).toContainText("I'm not sure what you mean.");

    await assistant.askAiButton(signedInPage).click();

    await expect(assistant.toolsUsed(signedInPage)).toContainText('Checked your orders');
    await expect(assistant.conversation(signedInPage)).toContainText('You have no open orders.');
  });
});
