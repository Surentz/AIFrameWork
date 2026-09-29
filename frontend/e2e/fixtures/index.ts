/* eslint-disable no-empty-pattern -- Playwright decides which fixtures to inject by parsing the
   destructuring pattern of a fixture's first parameter, so one that depends on nothing must
   still write `{}`. Replacing it with a named parameter changes what Playwright injects. */
import { existsSync, readFileSync } from 'node:fs';
import { test as base } from '@playwright/test';
import type { Browser, Page } from '@playwright/test';
import { connectionOptions, createApiClient, registerUser } from './api.ts';
import { ADMIN_STATE_PATH, ADMIN_USERNAME, PASSWORD } from '../support/identity.ts';
import type { ApiClient, TestUser } from './api.ts';

export type { TestUser } from './api.ts';

/**
 * A page signed in as `user`, in a context of its own.
 *
 * The context options are passed explicitly because a context built from `browser` directly does
 * not inherit the config's `use` block the way the built-in `page` fixture does — without this,
 * relative goto()s have no base URL and the kind run fails its TLS handshake.
 */
async function newSignedInPage(browser: Browser, user: TestUser): Promise<Page> {
  const context = await browser.newContext({ ...connectionOptions, storageState: user.state });
  return context.newPage();
}

interface WorkerFixtures {
  workerUser: TestUser;
  adminUser: TestUser;
}

interface TestFixtures {
  api: ApiClient;
  openSession: (user: TestUser) => Promise<Page>;
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
   * The operator, loaded from the session `e2e/setup/seed-admin.ts` signed in once for the whole
   * run — so it costs no auth call, however many workers there are.
   *
   * It holds Admin only because the target names `ADMIN_USERNAME` in the API's
   * `Admin__Usernames`: `playwright.config.ts` does for the stack it starts, and the kind overlay
   * does for the cluster. Configuration is the sole grant (ADR 0020), so this fixture cannot
   * promote anybody. A spec that needs it on an arbitrary URL target needs that target configured
   * the same way.
   */
  adminUser: [
    async ({}, use) => {
      // Named, because the alternative is a bare ENOENT with no pointer to where the file comes
      // from: a config without the globalSetup, or `npx playwright test` with a different one.
      if (!existsSync(ADMIN_STATE_PATH)) {
        throw new Error(
          `No saved operator session at ${ADMIN_STATE_PATH}. It is written by ` +
            'e2e/setup/seed-admin.ts, the globalSetup in playwright.config.ts - run through that config.',
        );
      }

      const state = JSON.parse(readFileSync(ADMIN_STATE_PATH, 'utf8')) as TestUser['state'];
      await use({ username: ADMIN_USERNAME, password: PASSWORD, state });
    },
    { scope: 'worker' },
  ],

  /** Catalogue writes go through `adminUser` — see `createApiClient`. */
  api: async ({ adminUser }, use) => {
    const client = createApiClient(adminUser);
    await use(client);
    await client.dispose();
  },

  /**
   * Further sessions within one test — a second browser for the same user, say. Every context it
   * opens is closed at teardown, pass or fail; closing them at the end of the test body instead
   * leaks both whenever an assertion fails first.
   */
  openSession: async ({ browser }, use) => {
    const pages: Page[] = [];
    await use(async (user) => {
      const page = await newSignedInPage(browser, user);
      pages.push(page);
      return page;
    });
    await Promise.all(pages.map((page) => page.context().close()));
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

  /** The default for an operator's screen: monitoring, fulfilment, the catalogue forms. */
  adminPage: async ({ browser, adminUser }, use) => {
    const page = await newSignedInPage(browser, adminUser);
    await use(page);
    await page.context().close();
  },
});

export { expect } from '@playwright/test';
