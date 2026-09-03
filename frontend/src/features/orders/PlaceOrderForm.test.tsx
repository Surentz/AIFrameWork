import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { PlaceOrderForm } from './PlaceOrderForm';

function renderForm(): void {
  render(
    <MemoryRouter>
      <PlaceOrderForm />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('PlaceOrderForm', () => {
  it('sends the typed values to the api', async () => {
    let body: unknown = null;
    server.use(
      http.post('/api/orders', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json('11111111-1111-1111-1111-111111111111', { status: 201 });
      }),
    );
    renderForm();

    await userEvent.type(screen.getByLabelText('Sku'), 'SKU-9');
    await userEvent.clear(screen.getByLabelText('Quantity'));
    await userEvent.type(screen.getByLabelText('Quantity'), '4');
    await userEvent.click(screen.getByRole('button', { name: 'Place order' }));

    await screen.findByRole('status');
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
});
