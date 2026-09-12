import { execFileSync } from 'node:child_process';
import { resolveTarget } from '../support/target.ts';

export default function globalTeardown(): void {
  const target = resolveTarget();

  // Dropping a database we did not create would be wrong against kind and catastrophic against
  // anything else.
  if (!target.managesStack) {
    console.log(`e2e: target '${target.name}' is not managed from here; leaving it alone.`);
    return;
  }

  // `down -v` runs even when the suite failed, which destroys exactly the evidence you want.
  // `npm run e2e:ui` sets this so the container survives between iterations.
  if (process.env.E2E_KEEP_DATABASE === '1') {
    console.log('e2e: E2E_KEEP_DATABASE is set; leaving the e2e Postgres up.');
    return;
  }

  execFileSync('docker', ['compose', '-f', '../docker-compose.e2e.yml', 'down', '-v'], {
    stdio: 'inherit',
  });
}
