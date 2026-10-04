import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { anAdminSession, aProduct, server } from './test/handlers';
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

  it('matches /orders/exports as the exports screen rather than as an order id', async () => {
    // Were "exports" captured as :id, the detail screen would request /api/orders/exports as if
    // it were one order.
    renderAt('/orders/exports');

    expect(await screen.findByRole('heading', { name: 'Exports' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Exports' })).toBeInTheDocument();
  });

  it('matches /products/new as the create screen rather than as an id', async () => {
    // Were "new" captured as :id, the detail screen would request /api/products/new. As an
    // administrator, because the create screen is theirs (ADR 0025).
    server.use(http.get('/api/auth/me', () => HttpResponse.json(anAdminSession)));

    renderAt('/products/new');

    expect(await screen.findByRole('heading', { name: 'Add a product' })).toBeInTheDocument();
  });

  it.each(['/products/new', `/products/${aProduct.id}/edit`])(
    'explains the refusal when an ordinary member opens %s',
    async (path) => {
      renderAt(path);

      expect(await screen.findByText(/This page is for administrators/)).toBeInTheDocument();
    },
  );

  it('renders the monitoring page, and its nav entry, for an administrator', async () => {
    server.use(http.get('/api/auth/me', () => HttpResponse.json(anAdminSession)));

    renderAt('/monitoring');

    expect(await screen.findByRole('heading', { name: 'Monitoring' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Monitoring' })).toBeInTheDocument();
  });

  it('keeps the monitoring nav entry out of an ordinary member\'s shell', async () => {
    // The default handler answers with a Member session. Hiding the entry is cosmetics - the API
    // refuses the endpoint regardless - but a link to a page you cannot open is still a bug.
    renderAt('/orders');

    expect(await screen.findByRole('link', { name: 'All orders' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Monitoring' })).not.toBeInTheDocument();
  });

  it('renders the fulfilment queue, and its nav entry, for an administrator', async () => {
    server.use(http.get('/api/auth/me', () => HttpResponse.json(anAdminSession)));

    renderAt('/fulfilment');

    expect(await screen.findByRole('heading', { name: 'Fulfilment' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Fulfilment' })).toBeInTheDocument();
  });

  it('keeps the fulfilment nav entry out of an ordinary member\'s shell', async () => {
    renderAt('/orders');

    expect(await screen.findByRole('link', { name: 'All orders' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Fulfilment' })).not.toBeInTheDocument();
  });

  it('explains the refusal when an ordinary member opens the fulfilment queue', async () => {
    // The default Member session, reaching the route by a typed address.
    renderAt('/fulfilment');

    expect(await screen.findByText(/This page is for administrators/)).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Fulfilment' })).not.toBeInTheDocument();
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
