import { defineConfig } from '@playwright/test';
import { API_PORT, E2E_CONNECTION_STRING, PREVIEW_PORT } from './e2e/env.ts';

export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/global-setup.ts',
  globalTeardown: './e2e/global-teardown.ts',
  use: { baseURL: `http://localhost:${PREVIEW_PORT}` },
  webServer: [
    {
      command: 'dotnet run --project ../src/Api --launch-profile http',
      // /health already exists, so readiness is a real check rather than a fixed wait.
      url: `http://localhost:${API_PORT}/health`,
      timeout: 120_000,
      reuseExistingServer: false,
      env: {
        ConnectionStrings__Default: E2E_CONNECTION_STRING,
        ASPNETCORE_URLS: `http://localhost:${API_PORT}`,
      },
    },
    {
      command: `npm run build && npm run preview -- --port ${PREVIEW_PORT}`,
      url: `http://localhost:${PREVIEW_PORT}`,
      timeout: 120_000,
      reuseExistingServer: false,
    },
  ],
});
