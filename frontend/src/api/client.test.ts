import { http, HttpResponse } from 'msw';
import { server } from '../test/handlers';
import { ApiError } from './client';
import { listOrders, placeOrder } from './orders';

describe('the api client', () => {
  it('returns the page on success', async () => {
    const page = await listOrders({ limit: 20 });

    expect(page.items).toHaveLength(2);
  });

  it('requests the limit and cursor as query parameters', async () => {
    let seen: string | null = null;
    server.use(
      http.get('/api/orders', ({ request }) => {
        seen = new URL(request.url).search;
        return HttpResponse.json({ items: [], nextCursor: null });
      }),
    );

    await listOrders({ limit: 5, cursor: 'abc' });

    expect(seen).toContain('limit=5');
    expect(seen).toContain('cursor=abc');
  });

  it('throws a typed ApiError carrying the problem title', async () => {
    server.use(
      http.get('/api/orders', () =>
        HttpResponse.json(
          { title: 'orders.limit_out_of_range', detail: 'Limit must be between 1 and 100.' },
          { status: 400 },
        ),
      ),
    );

    await expect(listOrders({ limit: 0 })).rejects.toThrow(ApiError);
  });

  it('exposes per-field validation messages from the problem errors map', async () => {
    server.use(
      http.post('/api/orders', () =>
        HttpResponse.json(
          {
            title: 'validation.failed',
            detail: 'Quantity must be positive.',
            errors: { Quantity: ['Quantity must be positive.'] },
          },
          { status: 400 },
        ),
      ),
    );

    const error = await placeOrder({ sku: 'SKU-1', quantity: 0 }).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).fieldErrors.Quantity).toEqual(['Quantity must be positive.']);
  });
});
