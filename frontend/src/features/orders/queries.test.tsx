import { render, renderHook, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { aPdf, aProduct, anOrderExport, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { OrderList } from './OrderList';
import { PlaceOrderForm } from './PlaceOrderForm';
import { useOrderExportDocument } from './queries';

// Covers the onSuccess invalidateQueries call in usePlaceOrder (queries.ts) - deleting that
// body breaks no other test, since PlaceOrderForm and OrderList are otherwise always tested in
// isolation, each with its own QueryClient. Mounting both under one shared client is what
// makes cross-component cache invalidation observable at all.
describe('placing an order and the order list', () => {
  it('refetches the list after a successful submit', async () => {
    let listRequests = 0;
    // Relies on the DEFAULT /api/products handler in test/handlers.ts, which offers aProduct
    // (sku SKU-1) in the picker.
    server.use(
      http.get('/api/orders', () => {
        listRequests += 1;
        const items =
          listRequests === 1
            ? [{ id: '1', sku: 'SKU-BEFORE', quantity: 1, placedAt: '2026-09-02T10:00:00+00:00' }]
            : [
                { id: '1', sku: 'SKU-BEFORE', quantity: 1, placedAt: '2026-09-02T10:00:00+00:00' },
                { id: '2', sku: aProduct.sku, quantity: 2, placedAt: '2026-09-02T11:00:00+00:00' },
              ];
        return HttpResponse.json({ items, nextCursor: null });
      }),
      http.post('/api/orders', () => HttpResponse.json('2', { status: 201 })),
    );

    render(
      <MemoryRouter>
        <PlaceOrderForm />
        <OrderList />
      </MemoryRouter>,
      { wrapper: withQueryClient() },
    );

    expect(await screen.findAllByRole('link', { name: /SKU-/ })).toHaveLength(1);

    // findByLabelText resolves as soon as the (initially empty, disabled) select mounts; the
    // catalogue arrives a tick later, so the option itself - not just the select - has to be
    // awaited before selecting it.
    await userEvent.selectOptions(
      await screen.findByLabelText('Product'),
      await screen.findByRole('option', { name: new RegExp(aProduct.sku) }),
    );
    await userEvent.clear(screen.getByLabelText('Quantity'));
    await userEvent.type(screen.getByLabelText('Quantity'), '2');
    await userEvent.click(screen.getByRole('button', { name: 'Place order' }));

    // A second row appearing is only possible if the mutation's onSuccess invalidated the list
    // query and OrderList refetched - the list handler above returns one row until its second
    // call.
    expect(await screen.findByRole('link', { name: aProduct.sku })).toBeInTheDocument();
    expect(listRequests).toBeGreaterThanOrEqual(2);
  });
});

describe('useOrderExportDocument', () => {
  it('fetches the export file as bytes from its download URL', async () => {
    let requested = '';
    server.use(
      http.get('/api/orders/exports/:id/download', ({ request }) => {
        requested = new URL(request.url).pathname;
        return HttpResponse.arrayBuffer(aPdf.slice().buffer);
      }),
    );

    const { result } = renderHook(() => useOrderExportDocument(anOrderExport.id), {
      wrapper: withQueryClient(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });
    expect(requested).toBe(`/api/orders/exports/${anOrderExport.id}/download`);
    expect(Array.from(new Uint8Array(result.current.data ?? new ArrayBuffer(0)))).toEqual(
      Array.from(aPdf),
    );
  });

  it('reports a missing export as an ApiError', async () => {
    server.use(
      http.get('/api/orders/exports/:id/download', () =>
        HttpResponse.json(
          { title: 'Not found', detail: 'That export does not exist or is not ready.' },
          { status: 404 },
        ),
      ),
    );

    const { result } = renderHook(() => useOrderExportDocument(anOrderExport.id), {
      wrapper: withQueryClient(),
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });
    expect(result.current.error?.status).toBe(404);
  });
});
