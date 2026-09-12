import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { aProduct, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { ProductList } from './ProductList';

function renderList(): void {
  render(
    <MemoryRouter>
      <ProductList />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('ProductList', () => {
  it('renders one row per product', async () => {
    renderList();

    expect(await screen.findByRole('link', { name: 'Widget' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Gadget' })).toBeInTheDocument();
  });

  it('announces the loading state before the products arrive', async () => {
    renderList();

    expect(screen.getByRole('status')).toHaveTextContent('Loading products…');

    expect(await screen.findByRole('link', { name: 'Widget' })).toBeInTheDocument();
  });

  it('formats the price to two decimal places', async () => {
    server.use(
      http.get('/api/products', () =>
        HttpResponse.json({ items: [{ ...aProduct, price: 5 }], nextCursor: null }),
      ),
    );

    renderList();

    // A regex, not '5.00': toLocaleString follows the runtime locale, so the decimal
    // separator is not ours to assume. Two decimal places is the part that is.
    expect(await screen.findByText(/^5[.,]00$/)).toBeInTheDocument();
  });

  it('formats a price the API sent as a string', async () => {
    // price is `number | string` in the contract, because ASP.NET Core's AllowReadingFromString
    // means the API genuinely accepts and documents both. Narrowing it would be wrong, so the
    // formatter has to cope — this is the test that says so.
    server.use(
      http.get('/api/products', () =>
        HttpResponse.json({ items: [{ ...aProduct, price: '12.5' }], nextCursor: null }),
      ),
    );

    renderList();

    expect(await screen.findByText(/^12[.,]50$/)).toBeInTheDocument();
  });

  it('offers no load-more control when the server reports no further pages', async () => {
    // The default handler returns nextCursor: null.
    renderList();

    expect(await screen.findByRole('link', { name: 'Widget' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('sends back the cursor the server issued and appends the page it returns', async () => {
    // Page two is reachable ONLY by sending cursor=CURSOR-1. A component that asked for the
    // next page without the cursor would be served page one again, Gizmo would never appear,
    // and this test would fail — which is why the handler is scripted on the cursor rather
    // than returning a fixed second page to any request.
    server.use(
      http.get('/api/products', ({ request }) => {
        const cursor = new URL(request.url).searchParams.get('cursor');

        if (cursor === null) {
          return HttpResponse.json({
            items: [{ ...aProduct, id: '1', name: 'Widget' }],
            nextCursor: 'CURSOR-1',
          });
        }

        if (cursor === 'CURSOR-1') {
          return HttpResponse.json({
            items: [{ ...aProduct, id: '3', name: 'Gizmo' }],
            nextCursor: null,
          });
        }

        throw new Error(`unexpected cursor: ${cursor}`);
      }),
    );

    renderList();

    await userEvent.click(await screen.findByRole('button', { name: 'Load more' }));

    expect(await screen.findByRole('link', { name: 'Gizmo' })).toBeInTheDocument();
    // Appended, not replaced.
    expect(screen.getByRole('link', { name: 'Widget' })).toBeInTheDocument();
    // nextCursor came back null, so there is nothing further to ask for.
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('renders an empty message when the catalogue is empty', async () => {
    server.use(http.get('/api/products', () => HttpResponse.json({ items: [], nextCursor: null })));

    renderList();

    expect(await screen.findByText('No products yet.')).toBeInTheDocument();
  });

  it('renders the failure instead of an empty list when the request fails', async () => {
    server.use(
      http.get('/api/products', () =>
        HttpResponse.json({ title: 'oops', detail: 'Could not load products.' }, { status: 500 }),
      ),
    );

    renderList();

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not load products.');
  });
});
