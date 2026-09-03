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
});
