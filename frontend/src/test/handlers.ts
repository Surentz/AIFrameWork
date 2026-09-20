import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import type { Session } from '../features/auth/types';
import type { Notification } from '../features/notifications/types';
import type { Order } from '../features/orders/types';
import type { Product } from '../features/products/types';

// Typed against the generated schema, not loose. An untyped fixture is how a renamed backend
// property leaves the frontend tests passing while the app breaks — the tests would keep
// asserting against a shape the API no longer returns. This makes that a build failure.
export const anOrder: Order = {
  id: '11111111-1111-1111-1111-111111111111',
  sku: 'SKU-1',
  quantity: 2,
  placedAt: '2026-09-02T10:00:00+00:00',
  // A string union, not a number: the API serializes enums by name, so the generated type is
  // 'Placed' | 'Shipped' | 'Cancelled'. Adding Status to the order reads is what makes the
  // ship/cancel endpoints observable at all — before that, a shipped order read back identically.
  status: 'Placed',
};

export const aProduct: Product = {
  id: '44444444-4444-4444-4444-444444444444',
  sku: 'SKU-1',
  name: 'Widget',
  description: 'A widget.',
  price: 9.99,
  createdAt: '2026-09-02T10:00:00+00:00',
  updatedAt: '2026-09-02T10:00:00+00:00',
};

// Unread by default (readAt absent). The read case is scripted per test, because "already read"
// is the branch that hides the Mark read button and that is worth asking for explicitly.
export const aNotification: Notification = {
  id: '66666666-6666-6666-6666-666666666666',
  kind: 'OrderPlaced',
  title: 'Order placed',
  body: 'Your order for 2 x SKU-1 is in.',
  subjectId: anOrder.id,
  createdAt: '2026-09-02T10:05:00+00:00',
};

export const aSession: Session = {
  userId: '33333333-3333-3333-3333-333333333333',
  username: 'ada',
  displayName: 'Ada Lovelace',
  role: 'Member',
};

/** The same person, promoted. For the screens that render differently for an administrator. */
export const anAdminSession: Session = { ...aSession, role: 'Admin' };

export const handlers = [
  http.post('/api/auth/login', () => HttpResponse.json(aSession)),
  http.get('/api/monitoring/jobs/health', () =>
    HttpResponse.json({
      running: 1,
      succeeded: 12,
      failed: 2,
      deadLettered: 1,
      since: '2026-09-20T00:00:00+00:00',
      triggerableJobs: ['PruneProcessedOutbox', 'PruneJobRuns'],
    }),
  ),
  http.get('/api/monitoring/jobs/runs', () =>
    HttpResponse.json({
      items: [
        {
          envelopeId: '77777777-7777-7777-7777-777777777777',
          attempt: 2,
          jobName: 'RebuildOrderReport',
          lane: 'Heavy',
          status: 'Failed',
          startedAt: '2026-09-20T10:00:00+00:00',
          completedAt: '2026-09-20T10:00:02+00:00',
          durationMs: 2000,
          ownerId: null,
          error: 'InvalidOperationException: the handler gave up',
          traceId: 'abc123',
          instanceId: 'worker-0',
        },
      ],
      totalCount: 1,
      page: 1,
    }),
  ),
  http.get('/api/monitoring/jobs/dead-letters', () =>
    HttpResponse.json({
      items: [
        {
          id: '88888888-8888-8888-8888-888888888888',
          messageType: 'RebuildOrderReport',
          exceptionType: 'InvalidOperationException',
          exceptionMessage: 'the handler gave up',
          sentAt: '2026-09-20T10:00:00+00:00',
          receivedAt: 'postgresql://jobs_heavy/',
          replayable: false,
        },
      ],
      totalCount: 1,
      page: 1,
    }),
  ),
  http.get('/api/monitoring/traffic', () =>
    HttpResponse.json({
      since: '2026-09-20T11:00:00+00:00',
      requestsPerMinute: 12.5,
      errorRate: 0.04,
      overall: {
        kind: 'Http',
        name: '',
        total: 750,
        failed: 30,
        faulted: 0,
        meanMs: 18,
        p50Ms: 9,
        p95Ms: 240,
        p99Ms: 900,
      },
      rows: [
        {
          kind: 'Http',
          name: 'GET /api/orders',
          total: 500,
          failed: 20,
          faulted: 0,
          meanMs: 15,
          p50Ms: 8,
          p95Ms: 180,
          p99Ms: 600,
        },
      ],
    }),
  ),
  http.get('/api/monitoring/traffic/series', () =>
    HttpResponse.json({
      since: '2026-09-20T11:00:00+00:00',
      points: [
        { bucketStart: '2026-09-20T11:58:00+00:00', total: 10, failed: 0, faulted: 0, p95Ms: 12 },
        { bucketStart: '2026-09-20T11:59:00+00:00', total: 14, failed: 2, faulted: 1, p95Ms: 240 },
      ],
    }),
  ),
  http.get('/api/monitoring/sign-ins/health', () =>
    HttpResponse.json({
      succeeded: 9,
      badCredentials: 3,
      lockedOut: 1,
      unknownUser: 4,
      since: '2026-09-20T00:00:00+00:00',
      activeUsers: 2,
      activeWindowMinutes: 15,
      lockedOutUsers: [
        {
          userId: '99999999-9999-9999-9999-999999999999',
          username: 'grace',
          lockedOutUntil: '2026-09-20T12:15:00+00:00',
        },
      ],
    }),
  ),
  http.get('/api/monitoring/sign-ins', () =>
    HttpResponse.json({
      items: [
        {
          id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
          at: '2026-09-20T11:59:00+00:00',
          userId: null,
          usernameAttempted: 'nosuchuser',
          outcome: 'UnknownUser',
          ipAddress: '203.0.113.7',
          userAgent: 'curl/8.7.1',
          traceId: 'def456',
        },
      ],
      totalCount: 1,
      page: 1,
    }),
  ),
  http.get('/api/monitoring/access', () =>
    HttpResponse.json({
      userId: anAdminSession.userId,
      username: anAdminSession.username,
      role: 'Admin',
    }),
  ),
  http.post('/api/auth/register', () => HttpResponse.json(aSession)),
  // Signed in by default: most screens under test live behind RequireAuth, and a suite that
  // had to sign in first would be testing the guard over and over instead of the screen.
  http.get('/api/auth/me', () => HttpResponse.json(aSession)),
  http.post('/api/auth/logout', () => new HttpResponse(null, { status: 204 })),
  http.post('/api/auth/change-password', () => new HttpResponse(null, { status: 204 })),
  http.post('/api/auth/sign-out-everywhere', () => new HttpResponse(null, { status: 204 })),
  http.get('/api/orders', () =>
    HttpResponse.json({
      items: [anOrder, { ...anOrder, id: '22222222-2222-2222-2222-222222222222', sku: 'SKU-2' }],
      nextCursor: null,
    }),
  ),
  http.get('/api/orders/:id', ({ params }) =>
    HttpResponse.json({ ...anOrder, id: String(params.id) }),
  ),
  http.post('/api/orders', () => HttpResponse.json(anOrder.id, { status: 201 })),
  http.get('/api/products', () =>
    HttpResponse.json({
      items: [
        aProduct,
        { ...aProduct, id: '55555555-5555-5555-5555-555555555555', sku: 'SKU-2', name: 'Gadget' },
      ],
      nextCursor: null,
    }),
  ),
  http.get('/api/products/:id', ({ params }) =>
    HttpResponse.json({ ...aProduct, id: String(params.id) }),
  ),
  http.post('/api/products', () => HttpResponse.json(aProduct.id, { status: 201 })),
  http.put('/api/products/:id', () => new HttpResponse(null, { status: 204 })),
  // All four notification endpoints, not just the ones a given screen uses: onUnhandledRequest
  // is 'error', and the bell's polling unread-count query rides along in every test that renders
  // AppLayout. A missing handler there would fail a suite that has nothing to do with the feed.
  http.get('/api/notifications', () =>
    HttpResponse.json({
      items: [
        aNotification,
        {
          ...aNotification,
          id: '77777777-7777-7777-7777-777777777777',
          kind: 'OrderShipped',
          title: 'Order shipped',
          readAt: '2026-09-02T11:00:00+00:00',
        },
      ],
      nextCursor: null,
    }),
  ),
  http.get('/api/notifications/unread-count', () => HttpResponse.json({ unreadCount: 1 })),
  http.post('/api/notifications/:id/read', () =>
    HttpResponse.json({ markedCount: 1, unreadCount: 0 }),
  ),
  http.post('/api/notifications/read-all', () =>
    HttpResponse.json({ markedCount: 1, unreadCount: 0 }),
  ),
  // SignalR's negotiate, answered the way a server with Realtime__Enabled off answers it: 404,
  // because Program.cs only calls MapHub inside that branch (pinned backend-side by
  // NotificationHubTests.Negotiate_WhenRealtimeIsOff_IsNotMappedAtAll). Off is the default
  // everywhere but a developer's machine, so this is the ordinary case, not a failure fixture -
  // every suite that mounts AppLayout takes this path and falls back to polling. It also has to
  // exist at all, since onUnhandledRequest is 'error'.
  http.post('/hubs/notifications/negotiate', () => new HttpResponse(null, { status: 404 })),
];

export const server = setupServer(...handlers);
