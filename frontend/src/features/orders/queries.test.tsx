import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { OrderList } from './OrderList';
import { PlaceOrderForm } from './PlaceOrderForm';

// Covers the onSuccess invalidateQueries call in usePlaceOrder (queries.ts) - deleting that
// body breaks no other test, since PlaceOrderForm and OrderList are otherwise always tested in
// isolation, each with its own QueryClient. Mounting both under one shared client is what
// makes cross-component cache invalidation observable at all.
describe('placing an order and the order list', () => {
  it('refetches the list after a successful submit', async () => {
    let listRequests = 0;
    server.use(
      http.get('/api/orders', () => {
        listRequests += 1;
        const items =
          listRequests === 1
            ? [{ id: '1', sku: 'SKU-BEFORE', quantity: 1, placedAt: '2026-09-02T10:00:00+00:00' }]
            : [
                { id: '1', sku: 'SKU-BEFORE', quantity: 1, placedAt: '2026-09-02T10:00:00+00:00' },
                { id: '2', sku: 'SKU-AFTER', quantity: 2, placedAt: '2026-09-02T11:00:00+00:00' },
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

    await userEvent.type(screen.getByLabelText('Sku'), 'SKU-AFTER');
    await userEvent.clear(screen.getByLabelText('Quantity'));
    await userEvent.type(screen.getByLabelText('Quantity'), '2');
    await userEvent.click(screen.getByRole('button', { name: 'Place order' }));

    // A second row appearing is only possible if the mutation's onSuccess invalidated the list
    // query and OrderList refetched - the list handler above returns one row until its second
    // call.
    expect(await screen.findByRole('link', { name: 'SKU-AFTER' })).toBeInTheDocument();
    expect(listRequests).toBeGreaterThanOrEqual(2);
  });
});
