// The single entry point for every e2e run. Three jobs: decide the target, make sure the database
// exists before Playwright starts anything, and take it down again after Playwright has exited.
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
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);

/** Runs a child to completion and returns its exit code. */
function spawn(command: string, args: readonly string[]): number {
  const result = spawnSync(command, [...args], { stdio: 'inherit' });

  if (result.error) {
    throw result.error;
  }
  return result.status ?? 1;
}

/** Runs a child, and ends this process with its code if it fails. */
function run(command: string, args: readonly string[]): void {
  const status = spawn(command, args);
  if (status !== 0) {
    process.exit(status);
  }
}

const argv = process.argv.slice(2);
const passthrough: string[] = [];
// Respect an inherited E2E_TARGET (e.g. `E2E_TARGET=kind npm run e2e` on bash/pwsh) so it is not
// silently downgraded to 'local'; an explicit --target below still wins over either.
let target = process.env.E2E_TARGET ?? 'local';

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

const playwrightArgs = ['test', ...passthrough];

// Anything we do not manage has production-shaped configuration: caching on, the real rate
// limit, and a database that keeps whatever earlier runs left behind.
//
// Appended after passthrough, not prepended: Playwright's CLI takes the last of a repeated
// flag, so if a caller also passes their own --grep-invert, THIS one wins and theirs is
// silently dropped. That is the safer direction to fail in (off-target still excludes
// @local-only specs) but it is easy to misread as the other way around.
if (target !== 'local') {
  playwrightArgs.push('--grep-invert', '@local-only');
}

// Not `npx`/`npx.cmd`: on Windows, spawnSync throws EINVAL invoking `npx.cmd` directly on some
// Node builds (reproduced here on Node 24.20.0, even bare with no arguments - a Node/Windows
// spawnSync regression, not anything about this project's arguments; see CVE-2024-27980, the
// batch-file command-injection fix that made `.cmd`/`.bat` spawning on Windows fragile). Routing
// through `cmd.exe /c` sidesteps that, but trades it for a worse bug: cmd.exe re-tokenizes the
// whole line by its OWN grammar, so a perfectly normal `--grep "@auth|@orders"` has its `|` read
// as a pipe instead of passed through - silent, Windows-only breakage on ordinary input.
// Resolving Playwright's own CLI entry point and running it directly with `process.execPath`
// avoids a shim, a shell, and the quoting question entirely, on every platform.
// Ctrl+C reaches Playwright too - it is in the same process group - and Playwright stops its own
// servers and exits. Ignoring it HERE is what lets the teardown below still run afterwards,
// instead of this process dying first and leaving the container behind.
process.on('SIGINT', () => undefined);

const status = spawn(process.execPath, [
  require.resolve('@playwright/test/cli'),
  ...playwrightArgs,
]);

// After Playwright has exited, pass or fail, so its webServers are already stopped - see
// teardown-database.ts for why this cannot be Playwright's globalTeardown.
if (target === 'local') {
  spawn(process.execPath, ['e2e/setup/teardown-database.ts']);
}

process.exit(status);
