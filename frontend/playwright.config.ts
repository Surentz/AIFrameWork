import { defineConfig, devices } from '@playwright/test';
import { API_PORT, E2E_CONNECTION_STRING, PREVIEW_PORT } from './e2e/support/env.ts';
import { resolveTarget } from './e2e/support/target.ts';

const target = resolveTarget();
const isCI = Boolean(process.env.CI);

export default defineConfig({
  testDir: './e2e/specs',
  globalTeardown: './e2e/setup/global-teardown.ts',

  fullyParallel: true,

  // With several people writing tests, a stray `test.only` silently shrinking CI to one test is
  // a matter of time.
  forbidOnly: isCI,

  retries: isCI ? 1 : 0,

  // Playwright's 5s default is tight for a cold .NET first request, more so through an ingress.
  expect: { timeout: 10_000 },
  timeout: target.managesStack ? 30_000 : 60_000,

  reporter: isCI
    ? [['list'], ['html', { open: 'never' }], ['github']]
    : [['list'], ['html', { open: 'never' }]],

  use: {
    baseURL: target.baseURL,
    ignoreHTTPSErrors: target.ignoreHTTPSErrors,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    // No `video` option: it is honored only by the built-in page/context fixtures'
    // _contextFactory, but signedInPage/isolatedPage (e2e/fixtures/index.ts's newSignedInPage) -
    // most of this suite - call browser.newContext() directly and would silently record
    // nothing, making a blanket `video: 'retain-on-failure'` a promise the config could not keep
    // for those tests. Trace and screenshot ARE captured for every context regardless of how it
    // was created, so failure diagnosis is not lost - just not doubled with video everywhere.
  },

  // One browser today. Named anyway: it labels the report, and adding firefox/webkit becomes a
  // three-line change rather than a restructure.
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],

  // Conditional spread, not `workers: cond ? undefined : 2`: exactOptionalPropertyTypes rejects
  // an explicit undefined for an optional property. Two workers off-target keeps registrations
  // inside the cluster's 10-per-60-seconds auth budget (one per worker, see e2e/CLAUDE.md).
  ...(target.managesStack ? {} : { workers: 2 }),

  ...(target.managesStack
    ? {
        webServer: [
          {
            // --no-launch-profile, not --launch-profile http: launchSettings.json hard-codes
            // applicationUrl to 5234 and wins over ASPNETCORE_URLS, so API_PORT was silently
            // ignored and the run hung for 120s. Setting the environment explicitly keeps it honest.
            command: 'dotnet run --project ../src/Api --no-launch-profile',
            // /health already exists, so readiness is a real check rather than a fixed wait.
            // (Only true here, where this reaches the API directly. Through the kind ingress
            // /health is served by nginx — see deploy/e2e-k8s.ps1.)
            url: `http://localhost:${API_PORT}/health`,
            timeout: 120_000,
            reuseExistingServer: false,
            // Playwright discards webServer output by default, which makes a start-up failure in
            // CI unreadable: all it reports is "Process from config.webServer was not able to
            // start. Exit code: 1", naming neither which server nor why.
            stdout: 'pipe',
            stderr: 'pipe',
            env: {
              ConnectionStrings__Default: E2E_CONNECTION_STRING,
              ASPNETCORE_URLS: `http://localhost:${API_PORT}`,
              ASPNETCORE_ENVIRONMENT: 'Development',
              // The same move ApiFactory makes. No appsettings file has a RateLimiting section,
              // so this API would run on the 10-per-60-seconds default - and every spec reaches
              // it through the preview proxy, so the limiter sees one address and one partition
              // for the whole suite.
              RateLimiting__Auth__PermitLimit: '1000000',
              // The same move ApiFactory makes. A cached page would turn the eviction path into a
              // source of intermittent failures in a suite that is not testing the cache.
              Cache__Enabled: 'false',
              // Off for the same reason the cache is, and it has to be said explicitly here:
              // this run sets ASPNETCORE_ENVIRONMENT=Development, so it would otherwise inherit
              // appsettings.Development.json's Realtime:Enabled=true. Push is best-effort by
              // contract (ADR 0019) and the feed is the truth, so a spec that asserted on the
              // feed would be racing a WebSocket it does not need. The REST path is what these
              // specs exercise.
              Realtime__Enabled: 'false',
            },
          },
          {
            command: `npm run build && npm run preview -- --port ${PREVIEW_PORT}`,
            url: `http://localhost:${PREVIEW_PORT}`,
            timeout: 120_000,
            reuseExistingServer: false,
            stdout: 'pipe',
            stderr: 'pipe',
          },
        ],
      }
    : {}),
});
