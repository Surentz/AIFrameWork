import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import type { Session } from '../features/auth/types';
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

export const aSession: Session = {
  userId: '33333333-3333-3333-3333-333333333333',
  username: 'ada',
  displayName: 'Ada Lovelace',
};

export const handlers = [
  http.post('/api/auth/login', () => HttpResponse.json(aSession)),
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
];

export const server = setupServer(...handlers);
