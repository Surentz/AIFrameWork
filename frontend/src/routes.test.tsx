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

  it('renders the dashboard at the root, with the sidebar beside it', async () => {
    renderAt('/');

    expect(await screen.findByRole('heading', { name: 'Dashboard', level: 1 })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Dashboard' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Exports, in Sales' })).toHaveAttribute(
      'href',
      '/orders/exports',
    );
  });

  it("keeps the operations group out of a member's sidebar and dashboard", async () => {
    renderAt('/');
    await screen.findByText(/Welcome back, Ada Lovelace/);

    expect(screen.queryByRole('button', { name: 'Operations' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Operations' })).not.toBeInTheDocument();
  });

  it('shows an administrator the operations group in both', async () => {
    server.use(http.get('/api/auth/me', () => HttpResponse.json(anAdminSession)));
    renderAt('/');

    expect(await screen.findByRole('button', { name: 'Operations' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Operations', level: 2 })).toBeInTheDocument();
  });

  it('renders the catalogue screens inside the app shell', async () => {
    renderAt('/products');

    expect(await screen.findByRole('link', { name: 'Widget' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Products' })).toBeInTheDocument();
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

  it("keeps the monitoring nav entry out of an ordinary member's shell", async () => {
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

  it("keeps the fulfilment nav entry out of an ordinary member's shell", async () => {
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

  it('opens the assistant for a member, with its icon in the header', async () => {
    server.use(http.get('/api/assistant', () => HttpResponse.json({ enabled: true })));
    renderAt('/assistant');

    expect(await screen.findByRole('textbox', { name: 'Your question' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Assistant' })).toHaveAttribute('href', '/assistant');
  });

  it('shows no assistant icon while the assistant is off', async () => {
    renderAt('/orders');

    await screen.findByRole('link', { name: 'SKU-1' });
    expect(screen.queryByRole('link', { name: 'Assistant' })).not.toBeInTheDocument();
  });

  it('sends the old monitoring address to the assistant', async () => {
    server.use(
      http.get('/api/auth/me', () => HttpResponse.json(anAdminSession)),
      http.get('/api/assistant', () => HttpResponse.json({ enabled: true })),
    );
    renderAt('/monitoring/assistant');

    expect(await screen.findByRole('navigation', { name: 'Conversations' })).toBeInTheDocument();
  });
});
