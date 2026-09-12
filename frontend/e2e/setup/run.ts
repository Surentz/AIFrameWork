// The single entry point for every e2e run. Two jobs: decide the target, and make sure the
// database exists before Playwright starts anything.
//
// The database prep deliberately stays OUTSIDE Playwright. Playwright launches `webServer`
// processes before `globalSetup` runs, so as a global setup it arrived too late and the API
// booted against a database that did not exist — see setup/prepare-database.ts's own header.
// Chaining it with `&&` in an npm script had the opposite failure: `npx playwright test` run
// directly (an IDE, `--ui`) skipped it silently. Doing it here keeps the ordering constraint and
// removes the footgun.
//
// It also sets E2E_TARGET itself, because `E2E_TARGET=kind npm run e2e` is bash syntax that does
// nothing in PowerShell — and adding cross-env for one variable is not worth a dependency.
import { spawnSync } from 'node:child_process';

function run(command: string, args: readonly string[]): void {
  const result = spawnSync(command, [...args], { stdio: 'inherit' });

  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}

const argv = process.argv.slice(2);
const passthrough: string[] = [];
let target = 'local';

for (let i = 0; i < argv.length; i += 1) {
  const arg = argv[i];

  if (arg === '--target') {
    const value = argv[i + 1];
    if (value === undefined) {
      throw new Error("--target needs a value: 'local', 'kind', or an http(s) URL.");
    }
    target = value;
    i += 1;
    continue;
  }

  if (arg !== undefined) {
    passthrough.push(arg);
  }
}

process.env.E2E_TARGET = target;

// UI mode is for iterating on one spec; tearing the container down after every run would make
// the next iteration pay for a fresh migrate.
if (passthrough.includes('--ui')) {
  process.env.E2E_KEEP_DATABASE = '1';
}

if (target === 'local') {
  run(process.execPath, ['e2e/setup/prepare-database.ts']);
}

const playwrightArgs = ['playwright', 'test', ...passthrough];

// Anything we do not manage has production-shaped configuration: caching on, the real rate
// limit, and a database that keeps whatever earlier runs left behind.
if (target !== 'local') {
  playwrightArgs.push('--grep-invert', '@local-only');
}

if (process.platform === 'win32') {
  // npx is a .cmd shim on Windows, and on this machine's Node build (v24.20.0) spawnSync
  // throws EINVAL invoking `npx.cmd` directly - reproducible with a bare, argument-free call,
  // so it is a Node/Windows spawnSync regression, not anything about this project's arguments.
  // Routing through cmd.exe's own /d /s /c sidesteps Node's `.cmd`-shim handling entirely,
  // without `shell: true`'s DEP0190 deprecation warning and unescaped-argument risk.
  run(process.env.ComSpec ?? 'cmd.exe', ['/d', '/s', '/c', 'npx', ...playwrightArgs]);
} else {
  run('npx', playwrightArgs);
}
