import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { OrderDetail } from './OrderDetail';

const id = '11111111-1111-1111-1111-111111111111';

function renderDetail(): void {
  render(
    <MemoryRouter initialEntries={[`/orders/${id}`]}>
      <Routes>
        <Route path="/orders/:id" element={<OrderDetail />} />
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('OrderDetail', () => {
  it('renders the sku as the heading', async () => {
    renderDetail();

    expect(await screen.findByRole('heading', { name: 'SKU-1' })).toBeInTheDocument();
  });

  it('announces the loading state before the order arrives', async () => {
    // Rendered through the real /orders/:id route (renderDetail uses <Routes>, not a bare
    // MemoryRouter) so useParams genuinely resolves an id and the query genuinely runs - a
    // bare-router render previously let an unreachable status element pass its test.
    renderDetail();

    expect(screen.getByRole('status')).toHaveTextContent('Loading order…');

    expect(await screen.findByRole('heading', { name: 'SKU-1' })).toBeInTheDocument();
  });

  it('renders the failure instead of a blank article when the order is missing', async () => {
    server.use(
      http.get('/api/orders/:id', () =>
        HttpResponse.json(
          { title: 'order.not_found', detail: `No order with id '${id}'.` },
          { status: 404 },
        ),
      ),
    );

    renderDetail();

    expect(await screen.findByRole('alert')).toHaveTextContent(`No order with id '${id}'.`);
  });

  it('shows the product name and unit price when the order has a snapshot', async () => {
    server.use(
      http.get('/api/orders/:id', () =>
        HttpResponse.json({
          id,
          sku: 'SKU-1',
          quantity: 2,
          placedAt: '2026-09-01T12:00:00Z',
          productId: 'p0000000-0000-4000-8000-000000000001',
          productName: 'Widget',
          unitPrice: 12.5,
        }),
      ),
    );

    renderDetail();

    expect(await screen.findByRole('heading', { name: 'Widget' })).toBeInTheDocument();
    // A regex, not '12.50': toLocaleString follows the runtime locale, so the decimal
    // separator is not ours to assume. Two decimal places is the part that is. Same pattern
    // as ProductList.test.tsx.
    expect(await screen.findByText(/^12[.,]50$/)).toBeInTheDocument();
    expect(screen.getByText(/^25[.,]00$/)).toBeInTheDocument();

    const skuLink = screen.getByRole('link', { name: 'SKU-1' });
    expect(skuLink).toHaveAttribute('href', '/products/p0000000-0000-4000-8000-000000000001');
  });

  it('falls back to the sku when the order predates the catalogue', async () => {
    // The default handler returns anOrder, which OMITS productId/productName/unitPrice
    // (undefined). The next test covers the API's other shape for the same case: explicit null.
    renderDetail();

    expect(await screen.findByRole('heading', { name: 'SKU-1' })).toBeInTheDocument();
    expect(screen.queryByText('Unit price')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'SKU-1' })).not.toBeInTheDocument();
  });

  it('falls back to the sku when the snapshot fields are explicit null', async () => {
    // The real API sends a literal JSON null for these fields on a legacy order, not an omitted
    // key - OrderResponse's members are `Guid?`/`string?`/`decimal?`, which System.Text.Json
    // serializes as `null`, never absent. Proves the `=== null` half of each guard, which the
    // previous test (relying on the fixture omitting the fields) does not exercise.
    server.use(
      http.get('/api/orders/:id', () =>
        HttpResponse.json({
          id,
          sku: 'SKU-1',
          quantity: 2,
          placedAt: '2026-09-01T12:00:00Z',
          productId: null,
          productName: null,
          unitPrice: null,
        }),
      ),
    );

    renderDetail();

    expect(await screen.findByRole('heading', { name: 'SKU-1' })).toBeInTheDocument();
    expect(screen.queryByText('Unit price')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'SKU-1' })).not.toBeInTheDocument();
  });
});
