import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { anOrder, server } from '../../test/handlers';
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

  it('offers no load-more control when the server reports no further pages', async () => {
    // The default handler returns nextCursor: null.
    renderList();

    expect(await screen.findAllByRole('link', { name: /SKU-/ })).toHaveLength(2);
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('sends back the cursor the server issued and appends the page it returns', async () => {
    // Page two is reachable ONLY by sending cursor=CURSOR-1. A component that asked for the
    // next page without the cursor would be served page one again, SKU-3 would never appear,
    // and this test would fail — which is why the handler is scripted on the cursor rather
    // than returning a fixed second page to any request.
    server.use(
      http.get('/api/orders', ({ request }) => {
        const cursor = new URL(request.url).searchParams.get('cursor');

        if (cursor === null) {
          return HttpResponse.json({
            items: [{ ...anOrder, id: '1', sku: 'SKU-1' }],
            nextCursor: 'CURSOR-1',
          });
        }

        if (cursor === 'CURSOR-1') {
          return HttpResponse.json({
            items: [{ ...anOrder, id: '3', sku: 'SKU-3' }],
            nextCursor: null,
          });
        }

        throw new Error(`unexpected cursor: ${cursor}`);
      }),
    );

    renderList();

    await userEvent.click(await screen.findByRole('button', { name: 'Load more' }));

    expect(await screen.findByRole('link', { name: 'SKU-3' })).toBeInTheDocument();
    // Appended, not replaced.
    expect(screen.getByRole('link', { name: 'SKU-1' })).toBeInTheDocument();
    // nextCursor came back null, so there is nothing further to ask for.
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
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

  it('shows the product name instead of the sku when the order has a snapshot', async () => {
    // The other tests all use anOrder, which carries no productName - so they already exercise
    // the fallback. This is the one place the positive case (name shown INSTEAD of sku) is proved.
    server.use(
      http.get('/api/orders', () =>
        HttpResponse.json({
          items: [{ ...anOrder, productName: 'Widget' }],
          nextCursor: null,
        }),
      ),
    );

    renderList();

    expect(await screen.findByRole('link', { name: 'Widget' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'SKU-1' })).not.toBeInTheDocument();
  });
});
