import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { aProduct, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { EditProductForm } from './EditProductForm';

function renderForm(): void {
  render(
    <MemoryRouter initialEntries={[`/products/${aProduct.id}/edit`]}>
      <Routes>
        <Route path="/products/:id/edit" element={<EditProductForm />} />
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('EditProductForm', () => {
  it('prefills the fields from the loaded product', async () => {
    renderForm();

    expect(await screen.findByLabelText('Name')).toHaveValue('Widget');
    expect(screen.getByLabelText('Description')).toHaveValue('A widget.');
    expect(screen.getByLabelText('Price')).toHaveValue('9.99');
  });

  it('shows the sku as a fact rather than an editable field', async () => {
    // The domain refuses to change a sku, so offering an input for it would be a lie.
    renderForm();

    expect(await screen.findByText(/set at creation and not editable/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Sku')).not.toBeInTheDocument();
  });

  it('puts the edited fields, and no sku', async () => {
    let body: unknown;
    server.use(
      http.put('/api/products/:id', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderForm();
    const name = await screen.findByLabelText('Name');
    await userEvent.clear(name);
    await userEvent.type(name, 'Gadget');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(body).toEqual({ name: 'Gadget', description: 'A widget.', price: '9.99' });
  });

  it('sends null when the description is cleared', async () => {
    let body: { description?: unknown } = {};
    server.use(
      http.put('/api/products/:id', async ({ request }) => {
        body = (await request.json()) as { description?: unknown };
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderForm();
    await userEvent.clear(await screen.findByLabelText('Description'));
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(body.description).toBeNull();
  });

  it('renders per-field validation messages against their fields', async () => {
    server.use(
      http.put('/api/products/:id', () =>
        HttpResponse.json(
          {
            title: 'validation.failed',
            detail: 'Name must not be empty.',
            errors: { Name: ['Name must not be empty.'] },
          },
          { status: 400 },
        ),
      ),
    );

    renderForm();
    await userEvent.clear(await screen.findByLabelText('Name'));
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('Name must not be empty.')).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toHaveAttribute('aria-invalid', 'true');
  });

  it('renders the failure when the product cannot be loaded', async () => {
    server.use(
      http.get('/api/products/:id', () =>
        HttpResponse.json(
          { title: 'product.not_found', detail: 'No product with that id.' },
          { status: 404 },
        ),
      ),
    );

    renderForm();

    expect(await screen.findByRole('alert')).toHaveTextContent('No product with that id.');
  });
});
