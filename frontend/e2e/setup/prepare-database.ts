// Brings the e2e database up, migrates it, and builds both .NET hosts. Run by the `e2e` npm script BEFORE
// `playwright test`, deliberately — this cannot be Playwright's `globalSetup`.
//
// Playwright launches `webServer` processes before it runs `globalSetup`, so as a global setup
// this work happened too late: the API started against a database that did not exist yet. That
// was survivable while the API only touched Postgres on the first request, but durable Wolverine
// now migrates its envelope schema during host startup (ADR 0005), so the API refuses to boot
// without a reachable database and Playwright reports only "Process from config.webServer was
// not able to start. Exit code: 1".
//
// It hid locally, too: anyone with the container already up from a previous run saw a green
// suite. CI, starting clean every time, failed on the first run.
//
// Teardown is teardown-database.ts, which run.ts runs after Playwright exits - not Playwright's
// globalTeardown, which runs before the webServers stop.
import { execFileSync } from 'node:child_process';
import { E2E_CONNECTION_STRING } from '../support/env.ts';

// dotnet-ef is a local tool (.config/dotnet-tools.json); without a restore this only works
// by accident, on a machine that also happens to have it installed globally.
execFileSync('dotnet', ['tool', 'restore'], { stdio: 'inherit' });

execFileSync('docker', ['compose', '-f', '../docker-compose.e2e.yml', 'up', '-d', '--wait'], {
  stdio: 'inherit',
});

execFileSync(
  'dotnet',
  [
    'ef',
    'database',
    'update',
    '--project',
    '../src/Infrastructure',
    // Infrastructure, not Api: DesignTimeDbContextFactory already builds the DbContext
    // without the Api composition root, and Api need not (and, per the reverted csproj
    // change, must not) reference Microsoft.EntityFrameworkCore.Design just to satisfy the
    // CLI's startup-project requirement.
    '--startup-project',
    '../src/Infrastructure',
  ],
  {
    stdio: 'inherit',
    env: { ...process.env, ConnectionStrings__Default: E2E_CONNECTION_STRING },
  },
);

// Both hosts are built HERE, once, and playwright.config.ts starts them with `--no-build`.
// Playwright launches its webServers in parallel, so two `dotnet run`s would each build the
// projects they share (Application, Infrastructure, Domain) at the same moment and race on the
// same obj/ files. A compile error is also readable here, where a webServer reports only "Process
// from config.webServer was not able to start. Exit code: 1".
for (const host of ['../src/Api', '../src/Worker']) {
  execFileSync('dotnet', ['build', host], { stdio: 'inherit' });
}
