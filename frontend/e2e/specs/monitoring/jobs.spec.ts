import { expect, test } from '../../fixtures/index.ts';
import * as monitoring from '../../screens/monitoring.ts';

/**
 * The jobs page against a real worker. `playwright.config.ts` starts `src/Worker` beside the API
 * because the API listens to no queue (ADR 0016): without it a triggered job is enqueued and
 * never runs, and the runs table has nothing to show.
 *
 * @local-only twice over: the administrator role (see monitoring.spec.ts), and the worker, which
 * an arbitrary URL target cannot be assumed to run.
 */

// Harmless to trigger at any moment: it deletes only outbox rows that were processed long ago,
// so no other spec's pending notification can be caught by it.
const Job = 'PruneProcessedOutbox';

test.describe('jobs', { tag: '@local-only' }, () => {
  test('runs a scheduled job on demand, and records the run', async ({
    adminPage,
    adminUser,
    api,
  }) => {
    // A count, not "a row exists": the job has a cron, and an earlier test or run may already
    // have left rows for it. What proves THIS trigger worked is one more success than before.
    const before = await api.countJobRuns(adminUser, Job, 'Succeeded');

    await adminPage.goto('/monitoring/jobs');
    await expect(monitoring.pageHeading(adminPage, 'Job runs')).toBeVisible();

    await test.step('trigger it', async () => {
      await monitoring.triggerJob(adminPage, Job);
      await expect(monitoring.triggerQueued(adminPage)).toBeVisible();
    });

    await test.step('the worker runs it to success', async () => {
      await expect
        .poll(() => api.countJobRuns(adminUser, Job, 'Succeeded'), { timeout: 20_000 })
        .toBeGreaterThan(before);
    });

    await test.step('the runs table shows it, filtered by job and status', async () => {
      await monitoring.runJobFilter(adminPage).fill(Job);
      await monitoring.runStatusFilter(adminPage).selectOption('Succeeded');

      // The panel re-reads on every filter change, so the new run is on screen without waiting
      // for the ten-second refresh.
      await expect(monitoring.runRows(adminPage, Job).first()).toContainText('Succeeded');
    });
  });

  test('only offers scheduled jobs to trigger', async ({ adminPage }) => {
    await adminPage.goto('/monitoring/jobs');

    // A job that takes arguments has none a trigger could supply, so it must not be offered.
    // SendOrderConfirmation and BuildOrderExport are the two such jobs today.
    const options = monitoring.triggerJobSelect(adminPage).getByRole('option');
    await expect(options.filter({ hasText: Job })).toHaveCount(1);
    await expect(options.filter({ hasText: 'SendOrderConfirmation' })).toHaveCount(0);
    await expect(options.filter({ hasText: 'BuildOrderExport' })).toHaveCount(0);
  });

  // The whole enqueue path, not just the trigger: OrderPlaced is handled off the outbox, which
  // enqueues SendOrderConfirmation, which the worker - a different process - picks up and runs.
  //
  // What this proves is the PIPELINE, not this order: specs run in parallel and several place
  // orders, and a job run does not record which order it confirmed. Any new success is enough.
  test('confirms a placed order on the worker', async ({ adminUser, api, workerUser }) => {
    const before = await api.countJobRuns(adminUser, 'SendOrderConfirmation', 'Succeeded');

    await api.placeOrders(workerUser, 1);

    await expect
      .poll(() => api.countJobRuns(adminUser, 'SendOrderConfirmation', 'Succeeded'), {
        timeout: 20_000,
      })
      .toBeGreaterThan(before);
  });

  test('shows the dead-letter panel', async ({ adminPage }) => {
    await adminPage.goto('/monitoring/jobs');

    // Structure only. Nothing in the suite dead-letters a message on purpose, and whether any
    // exists depends on what else this database has seen.
    await expect(adminPage.getByRole('heading', { name: 'Dead letters', level: 2 })).toBeVisible();
  });
});
