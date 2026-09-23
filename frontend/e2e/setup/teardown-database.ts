// Takes the e2e database down. Run by e2e/setup/run.ts AFTER the Playwright process has exited -
// deliberately not Playwright's `globalTeardown`.
//
// Playwright runs `globalTeardown` BEFORE it stops its `webServer`s. As a global teardown this
// removed Postgres from under a still-running API and worker, and every green run ended with both
// hosts logging "terminating connection due to administrator command" at `fail` level - three
// stack traces after "51 passed" that looked like a problem and were not one. Running it after
// Playwright has exited means the servers are already gone.
//
// The mirror image of prepare-database.ts, which run.ts runs BEFORE Playwright for the opposite
// ordering reason.
import { execFileSync } from 'node:child_process';
import { resolveTarget } from '../support/target.ts';

const target = resolveTarget();

// Dropping a database we did not create would be wrong against kind and catastrophic against
// anything else. run.ts only calls this for the local target; this guard is the second lock.
if (!target.managesStack) {
  console.log(`e2e: target '${target.name}' is not managed from here; leaving it alone.`);
} else if (process.env.E2E_KEEP_DATABASE === '1') {
  // `down -v` runs even when the suite failed, which destroys exactly the evidence you want.
  // `npm run e2e:ui` sets this so the container survives between iterations.
  console.log('e2e: E2E_KEEP_DATABASE is set; leaving the e2e Postgres up.');
} else {
  execFileSync('docker', ['compose', '-f', '../docker-compose.e2e.yml', 'down', '-v'], {
    stdio: 'inherit',
  });
}
