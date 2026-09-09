import { defineConfig } from '@playwright/test';
import { API_PORT, E2E_CONNECTION_STRING, PREVIEW_PORT } from './e2e/env.ts';

export default defineConfig({
  testDir: './e2e',
  globalTeardown: './e2e/global-teardown.ts',
  use: { baseURL: `http://localhost:${PREVIEW_PORT}` },
  webServer: [
    {
      // --no-launch-profile, not --launch-profile http: launchSettings.json hard-codes
      // applicationUrl to 5234 and wins over ASPNETCORE_URLS, so API_PORT was silently
      // ignored and the run hung for 120s. Setting the environment explicitly keeps it honest.
      command: 'dotnet run --project ../src/Api --no-launch-profile',
      // /health already exists, so readiness is a real check rather than a fixed wait.
      url: `http://localhost:${API_PORT}/health`,
      timeout: 120_000,
      reuseExistingServer: false,
      // Playwright discards webServer output by default, which makes a start-up failure in CI
      // unreadable: all it reports is "Process from config.webServer was not able to start.
      // Exit code: 1", naming neither which server nor why. Piping costs nothing locally and
      // is the difference between a diagnosable CI failure and a guessing game.
      stdout: 'pipe',
      stderr: 'pipe',
      env: {
        ConnectionStrings__Default: E2E_CONNECTION_STRING,
        ASPNETCORE_URLS: `http://localhost:${API_PORT}`,
        ASPNETCORE_ENVIRONMENT: 'Development',
        // The same move ApiFactory makes, for the same reason. No appsettings file has a
        // RateLimiting section, so this API would run on the 10-per-60-seconds default - and
        // every spec reaches it through the preview proxy, so the limiter sees one address and
        // one partition for the whole suite. auth.spec.ts alone spends 5 of those permits and
        // orders.spec.ts adds 2 per test in beforeEach. Exhausting it would surface as
        // sign-up.ts's waitForURL timing out: indistinguishable from a flake, and not
        // reproducible when re-running the one spec.
        RateLimiting__Auth__PermitLimit: '1000000',
        // The same move ApiFactory makes. The order specs place an order and then assert the
        // list contains it; a cached page would turn the eviction path into a source of
        // intermittent failures in a suite that is not testing the cache. Backend integration
        // tests cover it instead, with the cache deliberately on.
        Cache__Enabled: 'false',
      },
    },
    {
      command: `npm run build && npm run preview -- --port ${PREVIEW_PORT}`,
      url: `http://localhost:${PREVIEW_PORT}`,
      timeout: 120_000,
      reuseExistingServer: false,
      // Same reason as the API server above.
      stdout: 'pipe',
      stderr: 'pipe',
    },
  ],
});
