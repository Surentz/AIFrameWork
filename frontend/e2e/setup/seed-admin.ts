import { registerOrSignIn } from '../fixtures/api.ts';
import { ADMIN_USERNAME } from '../support/identity.ts';

/**
 * Registers the e2e operator ONCE, before any worker starts, so the `adminUser` fixture only ever
 * finds the account already there and signs in.
 *
 * Without this, every Playwright worker that needed `adminUser` registered the same fixed
 * username at the same moment, and all but one lost the race at the unique index. That race is
 * how the API's old 500-on-a-lost-race was found; the API now answers it with the 409
 * `registerOrSignIn` recovers from, so this is no longer load-bearing for correctness. It stays
 * because each lost race still costs a failed insert that EF logs at Error, which is noise in a
 * run's server log that looks like a failure.
 *
 * A `globalSetup`, and only on the stack Playwright manages. Unlike the database prep in
 * `run.ts`, this has to run AFTER the webServers are up, which is exactly when Playwright runs a
 * global setup. Off-target it is not configured at all: nothing there names this account an
 * administrator, and registering it would spend an auth permit for nothing.
 */
export default async function seedAdmin(): Promise<void> {
  await registerOrSignIn(ADMIN_USERNAME);
}
