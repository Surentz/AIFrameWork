import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { aProduct, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { CreateProductForm } from './CreateProductForm';

function renderForm(): void {
  render(
    <MemoryRouter>
      <CreateProductForm />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

async function fillAndSubmit(values: {
  sku?: string;
  name?: string;
  description?: string;
  price?: string;
}): Promise<void> {
  await userEvent.type(screen.getByLabelText('Sku'), values.sku ?? 'SKU-9');
  await userEvent.type(screen.getByLabelText('Name'), values.name ?? 'Widget');
  if (values.description !== undefined) {
    await userEvent.type(screen.getByLabelText('Description'), values.description);
  }

  await userEvent.type(screen.getByLabelText('Price'), values.price ?? '9.99');
  await userEvent.click(screen.getByRole('button', { name: 'Add product' }));
}

describe('CreateProductForm', () => {
  it('posts what was typed', async () => {
    let body: unknown;
    server.use(
      http.post('/api/products', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json(aProduct.id, { status: 201 });
      }),
    );

    renderForm();
    await fillAndSubmit({ sku: 'SKU-9', name: 'Gizmo', description: 'A gizmo.', price: '4.50' });

    expect(body).toEqual({
      sku: 'SKU-9',
      name: 'Gizmo',
      description: 'A gizmo.',
      price: '4.50',
    });
  });

  it('sends null rather than an empty string for an untouched description', async () => {
    // "" is not a description. The domain collapses it to null anyway, so saying so on the
    // wire keeps the two ends in agreement.
    let body: { description?: unknown } = {};
    server.use(
      http.post('/api/products', async ({ request }) => {
        body = (await request.json()) as { description?: unknown };
        return HttpResponse.json(aProduct.id, { status: 201 });
      }),
    );

    renderForm();
    await fillAndSubmit({});

    expect(body.description).toBeNull();
  });

  it('sends the price exactly as typed, without a float round-trip', async () => {
    // "1.005" has to reach the server's own decimal-places rule intact; converting to a number
    // here is how a value quietly becomes something the user did not type.
    let body: { price?: unknown } = {};
    server.use(
      http.post('/api/products', async ({ request }) => {
        body = (await request.json()) as { price?: unknown };
        return HttpResponse.json(aProduct.id, { status: 201 });
      }),
    );

    renderForm();
    await fillAndSubmit({ price: '1.005' });

    expect(body.price).toBe('1.005');
  });

  it('renders per-field validation messages against their fields', async () => {
    server.use(
      http.post('/api/products', () =>
        HttpResponse.json(
          {
            title: 'validation.failed',
            detail: 'Sku must not be empty.',
            errors: { Sku: ['Sku must not be empty.'], Price: ['Price must be positive.'] },
          },
          { status: 400 },
        ),
      ),
    );

    renderForm();
    await fillAndSubmit({});

    expect(await screen.findByText('Sku must not be empty.')).toBeInTheDocument();
    expect(screen.getByText('Price must be positive.')).toBeInTheDocument();
    expect(screen.getByLabelText('Sku')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByLabelText('Price')).toHaveAttribute('aria-invalid', 'true');
  });

  it('renders a conflict on a duplicate sku', async () => {
    server.use(
      http.post('/api/products', () =>
        HttpResponse.json(
          {
            title: 'product.sku_taken',
            detail: "The sku 'SKU-9' is already in the catalogue.",
          },
          { status: 409 },
        ),
      ),
    );

    renderForm();
    await fillAndSubmit({});

    expect(await screen.findByRole('alert')).toHaveTextContent(
      "The sku 'SKU-9' is already in the catalogue.",
    );
  });
});
