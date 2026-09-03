import { defineConfig } from '@playwright/test';

const connectionString =
  'Host=localhost;Port=55432;Database=aiframework_e2e;Username=e2e;Password=e2e';

export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/global-setup.ts',
  globalTeardown: './e2e/global-teardown.ts',
  use: { baseURL: 'http://localhost:4173' },
  webServer: [
    {
      command: 'dotnet run --project ../src/Api --launch-profile http',
      // /health already exists, so readiness is a real check rather than a fixed wait.
      url: 'http://localhost:5234/health',
      timeout: 120_000,
      reuseExistingServer: false,
      env: { ConnectionStrings__Default: connectionString },
    },
    {
      command: 'npm run build && npm run preview -- --port 4173',
      url: 'http://localhost:4173',
      timeout: 120_000,
      reuseExistingServer: false,
    },
  ],
});
