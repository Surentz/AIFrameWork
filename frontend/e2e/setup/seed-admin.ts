import { registerOrSignIn } from '../fixtures/api.ts';
import { ADMIN_USERNAME } from '../support/identity.ts';

/**
 * Registers the e2e operator ONCE, before any worker starts, so the `adminUser` fixture only ever
 * finds the account already there and signs in.
 *
 * Without this, every Playwright worker that needed `adminUser` registered the same fixed
 * username at the same moment. The API checks then inserts, so two registrations racing past the
 * check both reach the insert, and the loser's unique-index violation is answered with a 500 —
 * not the 409 `registerOrSignIn` recovers from. It took more specs needing an administrator
 * (ADR 0024) to make the race lose every run locally; CI's single worker never hit it.
 *
 * A `globalSetup`, and only on the stack Playwright manages. Unlike the database prep in
 * `run.ts`, this has to run AFTER the webServers are up, which is exactly when Playwright runs a
 * global setup. Off-target it is not configured at all: nothing there names this account an
 * administrator, and registering it would spend an auth permit for nothing.
 */
export default async function seedAdmin(): Promise<void> {
  await registerOrSignIn(ADMIN_USERNAME);
}
