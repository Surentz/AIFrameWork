import { expect, test } from '../../fixtures/index.ts';
import * as monitoring from '../../screens/monitoring.ts';

/**
 * The operator's view, end to end: a real cookie, a real role read from Postgres on every
 * request, and the real `[Authorize(Policy = "Monitoring")]` behind each panel.
 *
 * What is asserted here is STRUCTURE, not counts. The traffic tables are written by a flush that
 * runs on its own minute boundary (ADR 0021) and the job tiles count whatever earlier tests
 * happened to enqueue, so a numeric assertion would be a clock race. The numbers are pinned by
 * `Infrastructure.Tests/Monitoring` and `Api.IntegrationTests/Monitoring`, which own the
 * arithmetic; this owns "an administrator gets the page and a member does not".
 */
// Tagged @local-only: every test here needs the administrator role, and the only thing that
// grants it is Admin__Usernames — which only the stack playwright.config.ts starts names this
// account in. Against the kind cluster the same user would register as an ordinary member and
// every assertion below would fail on the refusal. See e2e/fixtures/index.ts's `adminUser`.
test.describe('monitoring', { tag: '@local-only' }, () => {
  test('shows the operator an overview of jobs, sign-ins and traffic', async ({ adminPage }) => {
    await adminPage.goto('/monitoring');

    await expect(monitoring.overviewHeading(adminPage)).toBeVisible();

    // Every group the overview promises. Present, not populated: the tile values are whatever
    // this database has seen, and asserting on them would be asserting on test ordering.
    await expect(monitoring.tiles(adminPage, 'Job health')).toBeVisible();
    await expect(monitoring.tiles(adminPage, 'Sign-in health')).toBeVisible();
    await expect(monitoring.tiles(adminPage, 'Traffic')).toBeVisible();

    // This spec's own sign-in is one of them, so "at least one success" is safe where an exact
    // count is not — the suite shares one database and one worker's admin registers once.
    await expect(monitoring.tile(adminPage, 'Sign-in health', 'Online now')).toHaveText(/^\d+$/);
  });

  test('drills down from the overview into traffic', async ({ adminPage }) => {
    await adminPage.goto('/monitoring');

    await monitoring.drillDown(adminPage, /Request rates, latency/).click();
    await adminPage.waitForURL('**/monitoring/traffic');

    await expect(adminPage.getByRole('heading', { name: 'Traffic', level: 1 })).toBeVisible();

    // Two charts, never one with two y-axes: requests and errors share a unit, latency does not.
    await expect(monitoring.chartTitle(adminPage, 'Requests and errors')).toBeVisible();
    await expect(monitoring.chartTitle(adminPage, 'Latency')).toBeVisible();

    await expect(monitoring.windowFilter(adminPage)).toHaveValue('60');
    await monitoring.windowFilter(adminPage).selectOption('1440');
    await expect(monitoring.windowFilter(adminPage)).toHaveValue('1440');

    // Rendered whether or not a bucket has been flushed yet, which is what makes it assertable.
    await expect(
      adminPage.getByRole('heading', { name: 'By endpoint and handler' }),
    ).toBeVisible();
  });

  test('links the operator to the jobs and sign-ins pages', async ({ adminPage }) => {
    await adminPage.goto('/monitoring');

    await monitoring.drillDown(adminPage, 'Job runs and dead letters').click();
    await adminPage.waitForURL('**/monitoring/jobs');
    await expect(adminPage.getByRole('heading', { name: 'Job runs', level: 1 })).toBeVisible();

    await adminPage.goto('/monitoring');
    await monitoring.drillDown(adminPage, /Sign-in history/).click();
    await adminPage.waitForURL('**/monitoring/logins');
    await expect(adminPage.getByRole('heading', { name: 'Sign-ins', level: 1 })).toBeVisible();
  });
});

test.describe('monitoring access', () => {
  test('refuses an ordinary member, and offers them no way in', async ({ signedInPage }) => {
    // Typed by hand: the nav entry is hidden for a member, so there is nothing to click.
    await signedInPage.goto('/monitoring');

    await expect(monitoring.refusal(signedInPage)).toContainText(
      'This page is for administrators',
    );
    await expect(monitoring.overviewHeading(signedInPage)).toBeHidden();

    // The gate the frontend one only explains. Reaching the API directly must fail the same way.
    const response = await signedInPage.request.get('/api/monitoring/jobs/health');
    expect(response.status()).toBe(403);
  });

  test('keeps the monitoring nav entry out of a member\'s shell', async ({ signedInPage }) => {
    await signedInPage.goto('/orders');

    await expect(monitoring.navLink(signedInPage)).toBeHidden();
  });
});
