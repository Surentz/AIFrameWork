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

  it('renders the catalogue screens inside the app shell', async () => {
    renderAt('/products');

    expect(await screen.findByRole('link', { name: 'Widget' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Catalogue' })).toBeInTheDocument();
  });

  it('renders the notifications feed inside the app shell, with the bell in the chrome', async () => {
    // The bell lives in AppLayout rather than on any one screen, so this is the only place that
    // pins it to the signed-in shell at all - a bell moved out of the layout would still pass
    // every test in features/notifications.
    renderAt('/notifications');

    expect(await screen.findByRole('heading', { name: 'Notifications' })).toBeInTheDocument();
    expect(
      await screen.findByRole('link', { name: 'Notifications, 1 unread' }),
    ).toBeInTheDocument();
  });

  it('matches /products/new as the create screen rather than as an id', async () => {
    // The route order is what decides this; declared the other way round, "new" would be
    // captured as :id and the detail screen would request /api/products/new.
    renderAt('/products/new');

    expect(await screen.findByRole('heading', { name: 'Add a product' })).toBeInTheDocument();
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
