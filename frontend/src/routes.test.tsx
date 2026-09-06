import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { withQueryClient } from './test/withQueryClient';
import { AppRoutes } from './routes';

function renderAt(path: string): void {
  render(
    <MemoryRouter initialEntries={[path]}>
      <AppRoutes />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('AppRoutes', () => {
  it('renders the orders screens inside the app shell', async () => {
    renderAt('/orders');

    expect(await screen.findByRole('link', { name: 'SKU-1' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'All orders' })).toBeInTheDocument();
  });

  // The reason /login sits outside the layout route: it is a full-bleed page, and rendering it
  // under the shell would put it beneath an "Orders" heading and the app nav.
  it('renders login outside the app shell, with a main landmark of its own', () => {
    renderAt('/login');

    expect(screen.getByRole('heading', { name: 'Welcome back' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'All orders' })).not.toBeInTheDocument();
    expect(screen.getByRole('main')).toBeInTheDocument();
  });
});
