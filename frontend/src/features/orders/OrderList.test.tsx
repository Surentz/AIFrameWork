import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { OrderList } from './OrderList';

function renderList(): void {
  render(
    <MemoryRouter>
      <OrderList />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('OrderList', () => {
  it('renders one row per order', async () => {
    renderList();

    expect(await screen.findAllByRole('link', { name: /SKU-/ })).toHaveLength(2);
  });

  it('announces the loading state before the orders arrive', async () => {
    renderList();

    expect(screen.getByRole('status')).toHaveTextContent('Loading orders…');

    expect(await screen.findAllByRole('link', { name: /SKU-/ })).toHaveLength(2);
  });

  it('renders an empty message when there are no orders', async () => {
    server.use(http.get('/api/orders', () => HttpResponse.json({ items: [], nextCursor: null })));

    renderList();

    expect(await screen.findByText('No orders yet.')).toBeInTheDocument();
  });

  it('renders the failure instead of an empty list when the request fails', async () => {
    server.use(
      http.get('/api/orders', () =>
        HttpResponse.json({ title: 'oops', detail: 'Could not load orders.' }, { status: 500 }),
      ),
    );

    renderList();

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not load orders.');
  });
});
