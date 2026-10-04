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
 * Signs in as a FIXED username, registering it only when it does not exist yet.
 *
 * Only the administrator needs this, and only because its name has to be known before the run
 * starts — the target's `Admin__Usernames` names it, and configuration is the sole grant of the
 * role (ADR 0020). `e2e/setup/seed-admin.ts` calls it exactly once per run; nothing else may.
 *
 * Sign-in FIRST, because the account almost always exists: `--ui` keeps the database, and the kind
 * cluster's Postgres keeps it across runs. That makes the usual cost one auth call, and a fresh
 * database two (a refused sign-in, then the registration) — which matters against the kind
 * cluster's 10-per-60-seconds budget. Registering first would cost two every time.
 *
 * Every other user in this suite is generated and registered exactly once — see `registerUser`.
 */
export async function signInOrRegister(username: string): Promise<TestUser> {
  const context = await request.newContext(connectionOptions);

  try {
    // rememberMe is `required` on LoginRequest, so omitting it is a 400 and not a default.
    const signIn = await context.post('/api/auth/login', {
      data: { username, password: PASSWORD, rememberMe: false },
    });

    if (!signIn.ok()) {
      if (signIn.status() !== 401) {
        throw new Error(
          `Signing in as '${username}' failed with ${String(signIn.status())}: ${await signIn.text()}`,
        );
      }

      // 401 is both "no such account" and "wrong password" (ADR 0006), so a registration that
      // then answers 409 means the account exists with a password this suite does not know.
      const registration = await context.post('/api/auth/register', {
        data: { username, password: PASSWORD, displayName: 'E2E Operator' },
      });

      if (!registration.ok()) {
        throw new Error(
          `'${username}' could not sign in, and registering it failed with ${String(registration.status())}: ${await registration.text()}`,
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

/** One row of `GET /api/notifications`. Only the fields a spec arranges or waits on. */
export interface FeedItem {
  readonly id: string;
  readonly kind:
    | 'OrderPlaced'
    | 'OrderShipped'
    | 'OrderCancelled'
    | 'ProductPriceChanged'
    | 'OrderExportReady';
  readonly title: string;
  readonly body: string;
  readonly subjectId?: string | null;
  readonly readAt?: string | null;
}

export interface ApiClient {
  register(): Promise<TestUser>;
  /**
   * One sign-in attempt with the wrong password, over HTTP. Spends an auth rate-limit permit and
   * one of the account's five attempts before lockout (User.MaxFailedSignInAttempts) - never
   * aim it at `workerUser`.
   */
  failSignIn(username: string): Promise<void>;
  /**
   * Creates a product for `sku` (named after it, priced 19.95) as the operator, then orders it as
   * `user`. Returns the order id.
   */
  placeOrder(user: TestUser, order: { sku: string; quantity: number }): Promise<string>;
  /** Orders a product ALREADY in the catalogue - see createProduct. Returns the order id. */
  orderProduct(user: TestUser, order: { sku: string; quantity: number }): Promise<string>;
  /** `count` orders with generated SKUs, in parallel. Returns the SKUs, newest-first order not guaranteed. */
  placeOrders(user: TestUser, count: number): Promise<readonly string[]>;
  /**
   * Ships any buyer's order through the fulfilment endpoint. Needs an administrator, so a spec
   * that calls it passes `adminUser` and carries `@local-only`. ADR 0024.
   */
  shipOrder(admin: TestUser, orderId: string): Promise<void>;
  cancelOrder(user: TestUser, orderId: string, reason: string): Promise<void>;
  /**
   * Returns the new product's id. Always as the operator the client was built with: writing to
   * the catalogue needs `Catalogue.Manage` (ADR 0025), so there is no user to choose.
   */
  createProduct(product: NewProduct): Promise<string>;
  /**
   * Replaces a product's editable fields, as the operator; a changed price notifies everyone who
   * ordered it.
   */
  updateProduct(
    id: string,
    product: { name: string; price: string; description?: string | null },
  ): Promise<void>;
  /**
   * Waits until `user`'s feed holds a notification of `kind` whose body contains `text`, and
   * returns it.
   *
   * Notifications are written by the outbox pump after the command that raised them has
   * committed, so they arrive a moment AFTER the request that caused them returns. Waiting here,
   * over HTTP, keeps that race out of the page: a spec that navigates only once this resolves
   * sees the row on first render, and never needs to reload in a loop.
   */
  waitForNotification(
    user: TestUser,
    match: { kind: FeedItem['kind']; text?: string; subjectId?: string },
  ): Promise<FeedItem>;
  /**
   * How many runs the job-runs table holds for `jobName`, optionally in one status. Needs an
   * administrator. A spec that triggers a job compares this before and after, because a
   * scheduled job may already have runs from its cron or an earlier test.
   */
  countJobRuns(admin: TestUser, jobName: string, status?: 'Succeeded' | 'Failed'): Promise<number>;
  dispose(): Promise<void>;
}

/**
 * Arrange-through-the-API, so a test that needs existing data pays milliseconds instead of a
 * form-fill per row. /api/orders carries no [EnableRateLimiting] — only the auth endpoints do —
 * so this is free even against the cluster.
 *
 * `operator` is the administrator every catalogue write goes through (ADR 0025). Its session was
 * signed in once for the whole run by `e2e/setup/seed-admin.ts`, so building a client costs no
 * auth call.
 */
export function createApiClient(operator: TestUser): ApiClient {
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
    await createProduct({ sku: order.sku, name: order.sku, price: '19.95' });

    return orderProduct(user, order);
  }

  async function orderProduct(
    user: TestUser,
    order: { sku: string; quantity: number },
  ): Promise<string> {
    const context = await contextFor(user);
    const response = await context.post('/api/orders', { data: order });

    if (!response.ok()) {
      throw new Error(
        `Placing '${order.sku}' failed with ${String(response.status())}: ${await response.text()}`,
      );
    }

    return (await response.json()) as string;
  }

  async function createProduct(product: NewProduct): Promise<string> {
    const context = await contextFor(operator);
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

  async function postOrFail(
    user: TestUser,
    url: string,
    what: string,
    options: { data?: unknown; method?: 'post' | 'put' } = {},
  ): Promise<void> {
    const context = await contextFor(user);
    const response = await context[options.method ?? 'post'](url, { data: options.data });

    if (!response.ok()) {
      throw new Error(
        `${what} failed with ${String(response.status())}: ${await response.text()}`,
      );
    }
  }

  async function waitForNotification(
    user: TestUser,
    match: { kind: FeedItem['kind']; text?: string; subjectId?: string },
  ): Promise<FeedItem> {
    // By subject when the caller has one: it names exactly one thing (an order, an export), where
    // a body can be shared - every export's ends "ready to download".
    const matches = (n: FeedItem): boolean =>
      n.kind === match.kind &&
      (match.text === undefined || n.body.includes(match.text)) &&
      (match.subjectId === undefined || n.subjectId === match.subjectId);
    const context = await contextFor(user);
    // The pump polls every second; ten is generous without hiding a notifier that never fires.
    const deadline = Date.now() + 10_000;

    for (;;) {
      const response = await context.get('/api/notifications?limit=50');
      if (!response.ok()) {
        throw new Error(
          `Reading the feed failed with ${String(response.status())}: ${await response.text()}`,
        );
      }

      const page = (await response.json()) as { items: readonly FeedItem[] };
      const found = page.items.find(matches);
      if (found !== undefined) {
        return found;
      }

      if (Date.now() > deadline) {
        throw new Error(
          `No ${match.kind} notification matching ${JSON.stringify(match)} reached ${user.username} within 10s.`,
        );
      }

      await new Promise((resolve) => setTimeout(resolve, 250));
    }
  }

  return {
    register: registerUser,
    async failSignIn(username) {
      const context = await request.newContext(connectionOptions);
      try {
        const response = await context.post('/api/auth/login', {
          data: { username, password: 'not the right password', rememberMe: false },
        });
        // Anything but the refusal means the arrange did not do what the spec thinks it did. A
        // locked account answers the same 401, so this cannot tell the fifth attempt apart.
        if (response.status() !== 401) {
          throw new Error(
            `A wrong password for '${username}' answered ${String(response.status())}: ${await response.text()}`,
          );
        }
      } finally {
        await context.dispose();
      }
    },
    placeOrder,
    orderProduct,
    createProduct,
    async shipOrder(admin, orderId) {
      await postOrFail(admin, `/api/fulfilment/orders/${orderId}/ship`, `Shipping order ${orderId}`);
    },
    async cancelOrder(user, orderId, reason) {
      await postOrFail(user, `/api/orders/${orderId}/cancel`, `Cancelling order ${orderId}`, {
        data: { reason },
      });
    },
    async updateProduct(id, product) {
      await postOrFail(operator, `/api/products/${id}`, `Updating product ${id}`, {
        method: 'put',
        data: { description: null, ...product },
      });
    },
    waitForNotification,
    async countJobRuns(admin, jobName, status) {
      const context = await contextFor(admin);
      const query = new URLSearchParams({ jobName });
      if (status !== undefined) {
        query.set('status', status);
      }

      const response = await context.get(`/api/monitoring/jobs/runs?${query.toString()}`);
      if (!response.ok()) {
        throw new Error(
          `Reading job runs failed with ${String(response.status())}: ${await response.text()}`,
        );
      }

      return Number(((await response.json()) as { totalCount: number | string }).totalCount);
    },
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
