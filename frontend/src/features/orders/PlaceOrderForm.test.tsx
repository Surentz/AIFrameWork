import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { OrderDetail } from './OrderDetail';
import { PlaceOrderForm } from './PlaceOrderForm';

function renderForm(): void {
  render(
    <MemoryRouter>
      <PlaceOrderForm />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

// Exercises the real route swap (/orders/new -> /orders/:id), the same way the app actually
// renders this form - a bare MemoryRouter with no <Routes> lets navigate() go nowhere, which
// would make a false-positive pass possible.
function renderFormWithRoutes(): void {
  render(
    <MemoryRouter initialEntries={['/orders/new']}>
      <Routes>
        <Route path="/orders/new" element={<PlaceOrderForm />} />
        <Route path="/orders/:id" element={<OrderDetail />} />
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('PlaceOrderForm', () => {
  it('sends the typed values to the api and lands on the order it placed', async () => {
    const id = '11111111-1111-1111-1111-111111111111';
    let body: unknown = null;
    server.use(
      http.post('/api/orders', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json(id, { status: 201 });
      }),
      http.get('/api/orders/:id', () =>
        HttpResponse.json({
          id,
          sku: 'SKU-9',
          quantity: 4,
          placedAt: '2026-09-02T10:00:00+00:00',
        }),
      ),
    );
    renderFormWithRoutes();

    await userEvent.type(screen.getByLabelText('Sku'), 'SKU-9');
    await userEvent.clear(screen.getByLabelText('Quantity'));
    await userEvent.type(screen.getByLabelText('Quantity'), '4');
    await userEvent.click(screen.getByRole('button', { name: 'Place order' }));

    expect(await screen.findByRole('heading', { name: 'SKU-9' })).toBeInTheDocument();
    expect(body).toEqual({ sku: 'SKU-9', quantity: 4 });
  });

  it('shows a server field error against the field it belongs to', async () => {
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
    renderForm();

    await userEvent.type(screen.getByLabelText('Sku'), 'SKU-9');
    await userEvent.click(screen.getByRole('button', { name: 'Place order' }));

    expect(await screen.findByText('Quantity must be positive.')).toBeInTheDocument();
  });

  it('shows the general failure when the request fails without field errors', async () => {
    server.use(
      http.post('/api/orders', () =>
        HttpResponse.json(
          { title: 'server.error', detail: 'Something went wrong.' },
          { status: 500 },
        ),
      ),
    );
    renderForm();

    await userEvent.type(screen.getByLabelText('Sku'), 'SKU-9');
    await userEvent.click(screen.getByRole('button', { name: 'Place order' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong.');
  });
});
