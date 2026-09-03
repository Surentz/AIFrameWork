import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';

export const anOrder = {
  id: '11111111-1111-1111-1111-111111111111',
  sku: 'SKU-1',
  quantity: 2,
  placedAt: '2026-09-02T10:00:00+00:00',
};

export const handlers = [
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
];

export const server = setupServer(...handlers);
