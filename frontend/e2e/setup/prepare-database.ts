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
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { E2E_ASSISTANT_KEY_FILE, E2E_CERT_DIR, E2E_CONNECTION_STRING } from '../support/env.ts';

// PowerShell 7 (pwsh) where it exists - CI, Linux, macOS - and Windows PowerShell 5.1 on a Windows
// machine without it. The fetch script runs under both.
function fetchAssistantModel(): void {
  const script = '../scripts/fetch-assistant-model.ps1';
  const hasPwsh = spawnSync('pwsh', ['-NoProfile', '-Command', 'exit 0']).status === 0;
  if (hasPwsh) {
    execFileSync('pwsh', ['-NoProfile', '-File', script], { stdio: 'inherit' });
  } else if (process.platform === 'win32') {
    execFileSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script], {
      stdio: 'inherit',
    });
  } else {
    throw new Error('PowerShell 7 (pwsh) is required to fetch the assistant model.');
  }
}

// dotnet-ef is a local tool (.config/dotnet-tools.json); without a restore this only works
// by accident, on a machine that also happens to have it installed globally.
execFileSync('dotnet', ['tool', 'restore'], { stdio: 'inherit' });
// The assistant's embedding model (ADR 0036): the API build below copies it beside its binaries,
// and instant answers are on in the stack Playwright starts.
fetchAssistantModel();

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

// Both hosts, and the partner simulator, are built HERE, once, and playwright.config.ts starts
// them with `--no-build`. Playwright launches its webServers in parallel, so two `dotnet run`s
// would each build the projects they share (Application, Infrastructure, Domain) at the same
// moment and race on the same obj/ files. A compile error is also readable here, where a
// webServer reports only "Process from config.webServer was not able to start. Exit code: 1".
for (const host of ['../src/Api', '../src/Worker', '../tests/PartnerSimulator']) {
  execFileSync('dotnet', ['build', host], { stdio: 'inherit' });
}

// The throwaway PKI for the simulator and the worker's client certificate, generated once and
// REUSED, not regenerated per run. On Windows the worker's SslStreamCertificateContext.Create
// copies the client's intermediate into CurrentUser\CA, and nothing removes it; every new PKI
// adds another "CN=AiFramework Dev Intermediate CA", and at about a hundred same-subject copies
// Windows chain building fails outright (see TestPki.RemoveFromUserStores). A reused PKI adds one.
//
// Regenerated when a file is missing, or when client.pfx is a year old. The client leaf is the
// shortest-lived certificate TestPki issues (two years; the intermediate four, the root five), and
// the certificate check reports Degraded within 30 days of its expiry, which would turn the
// simulator's row from "Healthy" and fail integrations.spec.ts. Half the leaf's life keeps that
// far away. CI runners start clean, so CI generates on every run.
const certFiles = ['ca.pem', 'client.pfx', 'client.pass', 'server.pfx', 'server.pass'];
const RegenerateAfterMs = 365 * 24 * 60 * 60 * 1000;
const certsMissing = certFiles.some((file) => !existsSync(path.join(E2E_CERT_DIR, file)));
const certsAgeing =
  !certsMissing &&
  Date.now() - statSync(path.join(E2E_CERT_DIR, 'client.pfx')).mtimeMs > RegenerateAfterMs;

if (certsMissing || certsAgeing) {
  execFileSync(
    'dotnet',
    [
      'run',
      '--project',
      '../tests/PartnerSimulator',
      '--no-build',
      '--',
      'generate-certs',
      E2E_CERT_DIR,
    ],
    { stdio: 'inherit' },
  );
} else {
  console.log(`Reusing the e2e partner PKI in ${E2E_CERT_DIR}.`);
}

// The assistant's key file. The fake Messages API ignores the key; the API only needs the file.
mkdirSync(path.dirname(E2E_ASSISTANT_KEY_FILE), { recursive: true });
writeFileSync(E2E_ASSISTANT_KEY_FILE, 'e2e-not-a-real-key\n');
