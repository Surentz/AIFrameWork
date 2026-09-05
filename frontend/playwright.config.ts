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
