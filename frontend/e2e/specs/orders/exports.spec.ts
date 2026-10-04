import { readFile } from 'node:fs/promises';
import { expect, test } from '../../fixtures/index.ts';
import * as exportsPage from '../../screens/exports.ts';

/**
 * An export of the caller's own orders, end to end: requested from the page, built by the worker
 * on the heavy lane, announced by the API's notifier, downloaded as a file (ADR 0029).
 *
 * @local-only: the build runs in the worker, which an arbitrary URL target cannot be assumed to
 * run - the same reason as jobs.spec.ts. Runs as `workerUser`, so it spends no auth permit.
 */
test.describe('order exports', { tag: '@local-only' }, () => {
  test('exports the caller\'s orders to a downloadable CSV', async ({ signedInPage, workerUser, api }) => {
    const sku = (await api.placeOrders(workerUser, 1))[0];
    if (sku === undefined) {
      throw new Error('placeOrders returned no sku');
    }

    const exportId = await test.step('request it from the exports page', async () => {
      await signedInPage.goto('/orders');
      await exportsPage.navLink(signedInPage).click();
      await expect(exportsPage.pageHeading(signedInPage)).toBeVisible();

      // The id from the request's own response, so the wait below is for THIS export. No
      // assertion on the in-progress button: a quick build can finish before the page ever
      // re-reads the list, and that is a pass, not a flake.
      const [response] = await Promise.all([
        signedInPage.waitForResponse(
          (r) => r.url().endsWith('/api/orders/exports') && r.request().method() === 'POST',
        ),
        exportsPage.exportButton(signedInPage).click(),
      ]);
      expect(response.status()).toBe(202);
      return ((await response.json()) as { id: string }).id;
    });

    await test.step('the worker builds it and the owner is told', async () => {
      // Over HTTP, then navigate (e2e/CLAUDE.md): the job, then the outbox pump, then the
      // notifier have to run, and none of that is the page's business to wait on. By subject:
      // workerUser keeps every notification, and an earlier export's would satisfy a body match.
      await api.waitForNotification(workerUser, { kind: 'OrderExportReady', subjectId: exportId });
    });

    await test.step('download it', async () => {
      await signedInPage.reload();

      const [download] = await Promise.all([
        signedInPage.waitForEvent('download'),
        exportsPage.downloadLink(signedInPage).click(),
      ]);

      expect(download.suggestedFilename()).toMatch(/^orders-\d{4}-\d{2}-\d{2}\.csv$/);
      const csv = await readFile(await download.path(), 'utf8');
      expect(csv).toContain('OrderId,Sku,Product,Quantity');
      expect(csv).toContain(sku);
    });
  });
});
