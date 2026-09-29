import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { anAdminSession, aProduct, server } from '../../test/handlers';
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

function requireElementById(id: string): HTMLElement {
  const element = document.getElementById(id);
  if (element === null) {
    throw new Error(`expected an element with id "${id}"`);
  }
  return element;
}

describe('PlaceOrderForm', () => {
  it('selects a product and lands on the order it placed', async () => {
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
          sku: aProduct.sku,
          quantity: 4,
          placedAt: '2026-09-02T10:00:00+00:00',
          productId: aProduct.id,
          productName: aProduct.name,
          unitPrice: aProduct.price,
        }),
      ),
    );
    renderFormWithRoutes();

    // Relies on the DEFAULT /api/products handler in test/handlers.ts, which returns aProduct.
    // findByLabelText resolves as soon as the (initially empty, disabled) select mounts; the
    // catalogue arrives a tick later, so the option itself - not just the select - has to be
    // awaited before selecting it.
    await userEvent.selectOptions(
      await screen.findByLabelText('Product'),
      await screen.findByRole('option', { name: new RegExp(aProduct.sku) }),
    );
    await userEvent.clear(screen.getByLabelText('Quantity'));
    await userEvent.type(screen.getByLabelText('Quantity'), '4');
    await userEvent.click(screen.getByRole('button', { name: 'Place order' }));

    expect(await screen.findByRole('heading', { name: aProduct.name })).toBeInTheDocument();
    await waitFor(() => {
      expect(body).toEqual({ sku: aProduct.sku, quantity: 4 });
    });
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

    // findByLabelText resolves as soon as the (initially empty, disabled) select mounts; the
    // catalogue arrives a tick later, so the option itself - not just the select - has to be
    // awaited before selecting it.
    await userEvent.selectOptions(
      await screen.findByLabelText('Product'),
      await screen.findByRole('option', { name: new RegExp(aProduct.sku) }),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Place order' }));

    await screen.findByText('Quantity must be positive.');

    const quantityInput = screen.getByLabelText('Quantity');
    expect(quantityInput).toHaveAttribute('aria-invalid', 'true');

    const describedBy = quantityInput.getAttribute('aria-describedby');
    if (describedBy === null) {
      throw new Error('expected the quantity input to have aria-describedby set');
    }
    const errorRegion = requireElementById(describedBy);
    expect(within(errorRegion).getByText('Quantity must be positive.')).toBeInTheDocument();

    // Pins the field-level placement: the generic role="alert" banner branch must not also
    // be rendering, or a bug that routes field errors to the banner would pass this test too.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
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

    // findByLabelText resolves as soon as the (initially empty, disabled) select mounts; the
    // catalogue arrives a tick later, so the option itself - not just the select - has to be
    // awaited before selecting it.
    await userEvent.selectOptions(
      await screen.findByLabelText('Product'),
      await screen.findByRole('option', { name: new RegExp(aProduct.sku) }),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Place order' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong.');
  });

  it('disables submit and explains itself when the catalogue is empty', async () => {
    server.use(http.get('/api/products', () => HttpResponse.json({ items: [], nextCursor: null })));
    renderForm();

    expect(await screen.findByText(/no products in the catalogue/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Place order' })).toBeDisabled();
  });

  it('tells a member an administrator adds products, with no link to a form they cannot use', async () => {
    server.use(http.get('/api/products', () => HttpResponse.json({ items: [], nextCursor: null })));
    renderForm();

    expect(await screen.findByText(/an administrator has to add one/i)).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /add a product/i })).not.toBeInTheDocument();
  });

  it('links an administrator to add a product when the catalogue is empty', async () => {
    server.use(
      http.get('/api/products', () => HttpResponse.json({ items: [], nextCursor: null })),
      http.get('/api/auth/me', () => HttpResponse.json(anAdminSession)),
    );
    renderForm();

    expect(await screen.findByRole('link', { name: /add a product/i })).toBeInTheDocument();
  });

  it('renders the error state when the catalogue cannot be loaded', async () => {
    server.use(
      http.get('/api/products', () =>
        HttpResponse.json(
          { title: 'server.error', detail: 'Could not load products.' },
          { status: 500 },
        ),
      ),
    );
    renderForm();

    expect(await screen.findByRole('alert')).toHaveTextContent(/products/i);
  });
});
