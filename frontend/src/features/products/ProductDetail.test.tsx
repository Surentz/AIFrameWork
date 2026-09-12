import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { aProduct, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { ProductDetail } from './ProductDetail';

function renderDetail(id = aProduct.id): void {
  render(
    <MemoryRouter initialEntries={[`/products/${id}`]}>
      <Routes>
        <Route path="/products/:id" element={<ProductDetail />} />
      </Routes>
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('ProductDetail', () => {
  it('renders the product', async () => {
    renderDetail();

    expect(await screen.findByRole('heading', { name: 'Widget' })).toBeInTheDocument();
    expect(screen.getByText(/^9[.,]99$/)).toBeInTheDocument();
    expect(screen.getByText('A widget.')).toBeInTheDocument();
  });

  it('says so rather than rendering a blank when there is no description', async () => {
    server.use(
      http.get('/api/products/:id', () => HttpResponse.json({ ...aProduct, description: null })),
    );

    renderDetail();

    expect(await screen.findByText('No description.')).toBeInTheDocument();
  });

  it('links to the edit screen for this product', async () => {
    renderDetail();

    const edit = await screen.findByRole('link', { name: 'Edit' });

    expect(edit).toHaveAttribute('href', `/products/${aProduct.id}/edit`);
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

    renderDetail();

    expect(await screen.findByRole('alert')).toHaveTextContent('No product with that id.');
  });
});
