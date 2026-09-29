import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { signInOrRegister } from '../fixtures/api.ts';
import { ADMIN_STATE_PATH, ADMIN_USERNAME } from '../support/identity.ts';

/**
 * Signs the e2e operator in ONCE per run, before any worker starts, and saves the session for the
 * `adminUser` fixture to load.
 *
 * Once, because of the auth budget. Nearly every spec arranges a product, and writing to the
 * catalogue needs an administrator (ADR 0025), so every worker needs this session. Signing in per
 * worker would spend a permit per worker against the kind cluster's 10-per-60-seconds limit;
 * loading a saved session spends none. Workers therefore never sign in as the operator, and never
 * register it — which also keeps them out of the registration race that used to 500.
 *
 * A `globalSetup` on every target, because it has to run AFTER the webServers are up on the
 * stack Playwright manages — exactly when Playwright runs a global setup. Off that stack, the
 * target must name `e2e-admin` in `Admin__Usernames` (the kind overlay does), or every spec that
 * arranges a product fails with a 403.
 */
export default async function seedAdmin(): Promise<void> {
  const operator = await signInOrRegister(ADMIN_USERNAME);

  mkdirSync(dirname(ADMIN_STATE_PATH), { recursive: true });
  writeFileSync(ADMIN_STATE_PATH, JSON.stringify(operator.state));
}
