import { expect, test } from '../../fixtures/index.ts';
import * as monitoring from '../../screens/monitoring.ts';

/**
 * The External systems page against a real mTLS partner and a dead address (ADR 0032). The worker
 * checks every 5 s in this run, so both rows settle within a few checks.
 */
// @local-only: needs the administrator role (see monitoring.spec.ts) and the worker and simulator
// only the managed stack starts.
test.describe('external systems', { tag: '@local-only' }, () => {
  test('shows the simulator healthy and the dead address unhealthy', async ({ adminPage }) => {
    // Triples the managed stack's 30 s test timeout: the waits below are 45 s each, because the
    // page refetches only every 30 s and the first render can predate the worker's first check.
    test.slow();

    await adminPage.goto('/monitoring/integrations');
    await expect(monitoring.pageHeading(adminPage, 'External systems')).toBeVisible();

    await expect(monitoring.systemRow(adminPage, 'PartnerSimulator')).toContainText('Healthy', {
      timeout: 45_000,
    });
    await expect(monitoring.systemRow(adminPage, 'Unreachable')).toContainText('Unhealthy', {
      timeout: 45_000,
    });
  });
});
