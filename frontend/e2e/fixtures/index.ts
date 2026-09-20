/* eslint-disable no-empty-pattern -- Playwright decides which fixtures to inject by parsing the
   destructuring pattern of a fixture's first parameter, so one that depends on nothing must
   still write `{}`. Replacing it with a named parameter changes what Playwright injects. */
import { test as base } from '@playwright/test';
import type { Browser, Page } from '@playwright/test';
import {
  connectionOptions,
  createApiClient,
  registerOrSignIn,
  registerUser,
} from './api.ts';
import { ADMIN_USERNAME } from '../support/identity.ts';
import type { ApiClient, TestUser } from './api.ts';

export type { TestUser } from './api.ts';

/**
 * A page signed in as `user`, in a context of its own.
 *
 * The context options are passed explicitly because a context built from `browser` directly does
 * not inherit the config's `use` block the way the built-in `page` fixture does — without this,
 * relative goto()s have no base URL and the kind run fails its TLS handshake.
 */
export async function newSignedInPage(browser: Browser, user: TestUser): Promise<Page> {
  const context = await browser.newContext({ ...connectionOptions, storageState: user.state });
  return context.newPage();
}

interface WorkerFixtures {
  workerUser: TestUser;
  adminUser: TestUser;
}

interface TestFixtures {
  api: ApiClient;
  freshUser: TestUser;
  signedInPage: Page;
  isolatedPage: Page;
  adminPage: Page;
}

export const test = base.extend<TestFixtures, WorkerFixtures>({
  /**
   * ONE registration for every test this worker runs. Auth calls therefore scale with worker
   * count, not test count — which is what keeps a large suite inside the real rate limit.
   *
   * The cost is that its data accumulates: never assert a list is empty or has an exact length
   * against this user. Take `freshUser` when a test needs a clean slate.
   */
  workerUser: [
    async ({}, use) => {
      await use(await registerUser());
    },
    { scope: 'worker' },
  ],

  /**
   * A user nobody else touches. Required — not merely preferred — for anything that rotates the
   * security stamp: changing a password, signing out everywhere, or tripping the lockout. Those
   * invalidate every cookie for that user, so doing them to `workerUser` breaks every later test
   * on the worker.
   */
  freshUser: async ({}, use) => {
    await use(await registerUser());
  },

  /**
   * The operator. Worker-scoped like `workerUser`, and for the same rate-limit reason.
   *
   * It holds Admin only because `playwright.config.ts` names `ADMIN_USERNAME` in the API's
   * `Admin__Usernames` — configuration is the sole grant (ADR 0020), so this fixture cannot
   * promote anybody, and a spec that needs it must carry `@local-only`: no off-target stack
   * names this account.
   */
  adminUser: [
    async ({}, use) => {
      await use(await registerOrSignIn(ADMIN_USERNAME));
    },
    { scope: 'worker' },
  ],

  api: async ({}, use) => {
    const client = createApiClient();
    await use(client);
    await client.dispose();
  },

  /** The default for a signed-in test. */
  signedInPage: async ({ browser, workerUser }, use) => {
    const page = await newSignedInPage(browser, workerUser);
    await use(page);
    await page.context().close();
  },

  /** For the session-invalidating tests described on `freshUser`. */
  isolatedPage: async ({ browser, freshUser }, use) => {
    const page = await newSignedInPage(browser, freshUser);
    await use(page);
    await page.context().close();
  },

  /** The default for a monitoring test. `@local-only` — see `adminUser`. */
  adminPage: async ({ browser, adminUser }, use) => {
    const page = await newSignedInPage(browser, adminUser);
    await use(page);
    await page.context().close();
  },
});

export { expect } from '@playwright/test';
