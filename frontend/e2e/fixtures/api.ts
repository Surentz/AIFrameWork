import { request } from '@playwright/test';
import type { APIRequestContext } from '@playwright/test';
import { resolveTarget } from '../support/target.ts';
import { PASSWORD, uniqueSku, uniqueUsername } from '../support/identity.ts';

/** Whatever APIRequestContext.storageState() returns — a cookie jar, held in memory. */
export type StorageState = Awaited<ReturnType<APIRequestContext['storageState']>>;

export interface TestUser {
  readonly username: string;
  readonly password: string;
  readonly state: StorageState;
}

const target = resolveTarget();

/** The options every context in the suite is built with. */
export const connectionOptions = {
  baseURL: target.baseURL,
  ignoreHTTPSErrors: target.ignoreHTTPSErrors,
} as const;

/**
 * Registers a user over HTTP and captures the session cookie.
 *
 * Over HTTP rather than through the registration form on purpose. The form is exercised by one
 * dedicated test; doing it here too would cost seconds per test and, worse, spend one of the
 * auth endpoint's rate-limit permits per test — which against the kind cluster's
 * 10-per-60-seconds budget caps the whole suite at roughly ten tests a minute.
 */
export async function registerUser(): Promise<TestUser> {
  const context = await request.newContext(connectionOptions);

  try {
    const username = uniqueUsername();
    const response = await context.post('/api/auth/register', {
      data: { username, password: PASSWORD, displayName: 'E2E Tester' },
    });

    if (!response.ok()) {
      // Named explicitly because the interesting failure here is a 429: it means the run is
      // making more auth calls than the target's limiter allows, and left unnamed it surfaces
      // downstream as an unrelated navigation timeout.
      throw new Error(
        `Registering '${username}' failed with ${String(response.status())}: ${await response.text()}`,
      );
    }

    return { username, password: PASSWORD, state: await context.storageState() };
  } finally {
    await context.dispose();
  }
}

/**
 * Registers a FIXED username, or signs in as it when it already exists.
 *
 * Only the administrator needs this, and only because its name has to be known before the run
 * starts — `playwright.config.ts` names it in the API's `Admin__Usernames`, which is the sole
 * grant of the role (ADR 0020). A fixed name collides two ways that a generated one cannot, and
 * both land here rather than in a spec: `--ui` keeps the database between runs, so the account
 * already exists on the second iteration; and two Playwright workers arranging in parallel both
 * pass the API's check-then-insert, so one of them gets the unique index's 409.
 *
 * Every other user in this suite is generated and registered exactly once — see `registerUser`.
 */
export async function registerOrSignIn(username: string): Promise<TestUser> {
  const context = await request.newContext(connectionOptions);

  try {
    const registration = await context.post('/api/auth/register', {
      data: { username, password: PASSWORD, displayName: 'E2E Operator' },
    });

    if (!registration.ok()) {
      if (registration.status() !== 409) {
        throw new Error(
          `Registering '${username}' failed with ${String(registration.status())}: ${await registration.text()}`,
        );
      }

      // rememberMe is `required` on LoginRequest, so omitting it is a 400 and not a default.
      const signIn = await context.post('/api/auth/login', {
        data: { username, password: PASSWORD, rememberMe: false },
      });

      if (!signIn.ok()) {
        throw new Error(
          `'${username}' exists but signing in failed with ${String(signIn.status())}: ${await signIn.text()}`,
        );
      }
    }

    return { username, password: PASSWORD, state: await context.storageState() };
  } finally {
    await context.dispose();
  }
}

export interface NewProduct {
  readonly sku: string;
  readonly name: string;
  readonly description?: string | null;
  readonly price: string;
}

export interface ApiClient {
  register(): Promise<TestUser>;
  placeOrder(user: TestUser, order: { sku: string; quantity: number }): Promise<string>;
  /** `count` orders with generated SKUs, in parallel. Returns the SKUs, newest-first order not guaranteed. */
  placeOrders(user: TestUser, count: number): Promise<readonly string[]>;
  /** Returns the new product's id. The catalogue is global, so any signed-in user may add to it. */
  createProduct(user: TestUser, product: NewProduct): Promise<string>;
  dispose(): Promise<void>;
}

/**
 * Arrange-through-the-API, so a test that needs existing data pays milliseconds instead of a
 * form-fill per row. /api/orders carries no [EnableRateLimiting] — only the auth endpoints do —
 * so this is free even against the cluster.
 */
export function createApiClient(): ApiClient {
  const contexts = new Map<string, Promise<APIRequestContext>>();

  function contextFor(user: TestUser): Promise<APIRequestContext> {
    const existing = contexts.get(user.username);
    if (existing !== undefined) {
      return existing;
    }

    const created = request.newContext({ ...connectionOptions, storageState: user.state });
    contexts.set(user.username, created);
    return created;
  }

  async function placeOrder(
    user: TestUser,
    order: { sku: string; quantity: number },
  ): Promise<string> {
    // PlaceOrder now refuses a sku the catalogue does not hold, so this creates the product it
    // is about to order first — keeping placeOrder a one-call arrange for every existing caller
    // instead of pushing a createProduct call onto each of them.
    await createProduct(user, { sku: order.sku, name: order.sku, price: '19.95' });

    const context = await contextFor(user);
    const response = await context.post('/api/orders', { data: order });

    if (!response.ok()) {
      throw new Error(
        `Placing '${order.sku}' failed with ${String(response.status())}: ${await response.text()}`,
      );
    }

    return (await response.json()) as string;
  }

  async function createProduct(user: TestUser, product: NewProduct): Promise<string> {
    const context = await contextFor(user);
    const response = await context.post('/api/products', {
      data: { description: null, ...product },
    });

    if (!response.ok()) {
      throw new Error(
        `Creating '${product.sku}' failed with ${String(response.status())}: ${await response.text()}`,
      );
    }

    return (await response.json()) as string;
  }

  return {
    register: registerUser,
    placeOrder,
    createProduct,
    async placeOrders(user, count) {
      const skus = Array.from({ length: count }, () => uniqueSku());
      await Promise.all(skus.map((sku) => placeOrder(user, { sku, quantity: 1 })));
      return skus;
    },
    async dispose() {
      await Promise.all([...contexts.values()].map(async (c) => (await c).dispose()));
      contexts.clear();
    },
  };
}
