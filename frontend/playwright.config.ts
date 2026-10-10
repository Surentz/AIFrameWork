import path from 'node:path';
import { defineConfig, devices } from '@playwright/test';
import {
  API_PORT,
  E2E_CERT_DIR,
  E2E_CONNECTION_STRING,
  E2E_RABBITMQ_URL,
  PREVIEW_PORT,
  SIMULATOR_HEALTH_PORT,
  SIMULATOR_PORT,
  WORKER_PORT,
} from './e2e/support/env.ts';
import { ADMIN_USERNAME } from './e2e/support/identity.ts';
import { resolveTarget } from './e2e/support/target.ts';

const target = resolveTarget();
const isCI = Boolean(process.env.CI);

/**
 * How much the API and the worker print while the suite runs. At the hosts' own Information
 * default, EF Core logs every SQL command it executes - about 1,900 of them, twelve thousand lines
 * per run - which buried the test results in CI. Warning still shows every failure: an unhandled
 * exception is logged at Error by GlobalExceptionHandler. Set E2E_SERVER_LOG_LEVEL=Information
 * (or Debug) to see everything again while chasing a problem.
 */
const serverLogLevel = process.env.E2E_SERVER_LOG_LEVEL ?? 'Warning';

/** Shared by both .NET hosts. The Lifetime category keeps its "Now listening on ..." lines. */
const serverLogging = {
  Logging__LogLevel__Default: serverLogLevel,
  'Logging__LogLevel__Microsoft.Hosting.Lifetime': 'Information',
};

export default defineConfig({
  testDir: './e2e/specs',
  // No globalTeardown: it runs BEFORE the webServers stop, so the database went away under a live
  // API and worker. e2e/setup/run.ts tears it down after Playwright exits instead.

  // Signs the operator in once for the run, on every target: see the file. On the stack Playwright
  // manages it runs after the webServers are up, which is why it is a global setup at all.
  globalSetup: './e2e/setup/seed-admin.ts',

  fullyParallel: true,

  // With several people writing tests, a stray `test.only` silently shrinking CI to one test is
  // a matter of time.
  forbidOnly: isCI,

  retries: isCI ? 1 : 0,

  // Playwright's 5s default is tight for a cold .NET first request, more so through an ingress.
  expect: { timeout: 10_000 },
  timeout: target.managesStack ? 30_000 : 60_000,

  // CI also writes a JSON report for the PR comment (.github/scripts/pr-report.js): it is the one
  // format that tells a flaky test (passed on retry) from a passing one. Its own file, not inside
  // playwright-report/, which the html reporter clears.
  reporter: isCI
    ? [
        ['list'],
        ['html', { open: 'never' }],
        ['github'],
        ['json', { outputFile: 'e2e-results.json' }],
      ]
    : [['list'], ['html', { open: 'never' }]],

  use: {
    baseURL: target.baseURL,
    ignoreHTTPSErrors: target.ignoreHTTPSErrors,
    // Locally there are no retries, so 'on-first-retry' would never record anything. CI keeps
    // it: a trace per failed first attempt is what the retry is for, without tracing every pass.
    trace: isCI ? 'on-first-retry' : 'retain-on-failure',
    screenshot: 'only-on-failure',
    // No `video` option: it is honored only by the built-in page/context fixtures'
    // _contextFactory, but signedInPage/isolatedPage/adminPage/openSession
    // (e2e/fixtures/index.ts's newSignedInPage) - most of this suite - call
    // browser.newContext() directly and would silently record
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
            // --no-build: e2e/setup/prepare-database.ts has already built both hosts, once - see
            // its closing comment for why two parallel `dotnet run` builds are a race.
            command: 'dotnet run --project ../src/Api --no-launch-profile --no-build',
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
              ...serverLogging,
              ConnectionStrings__Default: E2E_CONNECTION_STRING,
              // The e2e broker in docker-compose.e2e.yml. Both hosts refuse to start without one
              // (ADR 0026): the API publishes, the worker publishes and listens.
              ConnectionStrings__RabbitMq: E2E_RABBITMQ_URL,
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
              // The ONLY grant of the administrator role (ADR 0020), which is why the monitoring
              // specs are @local-only: no off-target stack names this account. Double
              // underscores and the __0 index, like every other key here - a single underscore
              // binds nothing and warns nothing, and the list is bound as a collection.
              Admin__Usernames__0: ADMIN_USERNAME,
            },
          },
          {
            // The job host (ADR 0016). The API listens to no queue, so without this every job the
            // stack enqueues - a scheduled job run from the monitoring page, an order confirmation
            // - sits in Postgres forever and the job-runs table never has a row to show.
            command: 'dotnet run --project ../src/Worker --no-launch-profile --no-build',
            url: `http://localhost:${WORKER_PORT}/health`,
            timeout: 120_000,
            reuseExistingServer: false,
            stdout: 'pipe',
            stderr: 'pipe',
            env: {
              ...serverLogging,
              ConnectionStrings__Default: E2E_CONNECTION_STRING,
              // The e2e broker in docker-compose.e2e.yml. Both hosts refuse to start without one
              // (ADR 0026): the API publishes, the worker publishes and listens.
              ConnectionStrings__RabbitMq: E2E_RABBITMQ_URL,
              ASPNETCORE_URLS: `http://localhost:${WORKER_PORT}`,
              ASPNETCORE_ENVIRONMENT: 'Development',
              // Two external systems for the External systems page: the simulator over mTLS, and
              // an address nothing listens on. Checked every 5 s instead of every minute (ADR 0032).
              ExternalSystems__Systems__PartnerSimulator__BaseAddress: `https://127.0.0.1:${SIMULATOR_PORT}/`,
              ExternalSystems__Systems__PartnerSimulator__Probe__Path: 'ping',
              ExternalSystems__Systems__PartnerSimulator__ClientCertificate__Path: path.join(
                E2E_CERT_DIR,
                'client.pfx',
              ),
              ExternalSystems__Systems__PartnerSimulator__ClientCertificate__PasswordFile:
                path.join(E2E_CERT_DIR, 'client.pass'),
              ExternalSystems__Systems__PartnerSimulator__ServerTrust__CaBundlePath: path.join(
                E2E_CERT_DIR,
                'ca.pem',
              ),
              ExternalSystems__Systems__PartnerSimulator__ServerTrust__CheckRevocation: 'false',
              ExternalSystems__Systems__Unreachable__BaseAddress: 'https://127.0.0.1:1/',
              Monitoring__ExternalSystemStatusPeriod: '00:00:05',
            },
          },
          {
            // A real mTLS partner for the External systems page (ADR 0032). Built by
            // prepare-database.ts; certificates generated there too.
            command: `dotnet run --project ../tests/PartnerSimulator --no-build -- serve "${E2E_CERT_DIR}" ${SIMULATOR_PORT} ${SIMULATOR_HEALTH_PORT}`,
            url: `http://127.0.0.1:${SIMULATOR_HEALTH_PORT}/health`,
            timeout: 60_000,
            reuseExistingServer: false,
            stdout: 'pipe',
            stderr: 'pipe',
            // At Information it logs five lines per probe, every 5 s, for the whole run.
            env: serverLogging,
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
