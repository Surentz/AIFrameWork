import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { aBuildingExport, anOrderExport, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { OrderExportsPage } from './OrderExportsPage';

function renderPage(): void {
  render(
    <MemoryRouter>
      <OrderExportsPage />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

function listExports(...exports: object[]): void {
  server.use(http.get('/api/orders/exports', () => HttpResponse.json(exports)));
}

describe('OrderExportsPage', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('links a ready export to its download', async () => {
    renderPage();

    const link = await screen.findByRole('link', { name: /^Download export requested / });
    expect(link).toHaveAttribute('href', `/api/orders/exports/${anOrderExport.id}/download`);
  });

  it('shows how many orders a ready export holds', async () => {
    renderPage();

    expect(await screen.findByRole('cell', { name: '2' })).toBeInTheDocument();
  });

  it('keeps the export button focusable but inert while an export is being built', async () => {
    // aria-disabled, not disabled: a disabled button drops keyboard focus to the page the moment
    // it is clicked, and that is exactly when the user is waiting on it.
    listExports(aBuildingExport);

    renderPage();

    const button = await screen.findByRole('button', { name: 'Export in progress…' });
    expect(button).toHaveAttribute('aria-disabled', 'true');
    expect(button).toBeEnabled();
  });

  it('announces the newest export\'s state to assistive technology', async () => {
    listExports(aBuildingExport);

    renderPage();

    // By its text: while loading, the page's only status region says "Loading exports…".
    expect(await screen.findByText('Your export is being built.')).toHaveAttribute('role', 'status');
  });

  it('offers to try again when the newest export failed', async () => {
    listExports({ ...aBuildingExport, status: 'Failed' });

    renderPage();

    expect(await screen.findByRole('button', { name: 'Try again' })).not.toHaveAttribute(
      'aria-disabled',
      'true',
    );
  });

  it('re-reads the list while an export is being built, and stops once it is ready', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let reads = 0;
    server.use(
      http.get('/api/orders/exports', () => {
        reads += 1;
        return HttpResponse.json(reads === 1 ? [aBuildingExport] : [{ ...aBuildingExport, status: 'Ready', rowCount: 3 }]);
      }),
    );
    renderPage();
    expect(await screen.findByRole('button', { name: 'Export in progress…' })).toBeInTheDocument();

    await vi.advanceTimersByTimeAsync(5_000);

    expect(await screen.findByRole('link', { name: /^Download export requested / })).toBeInTheDocument();
    const readsWhenReady = reads;
    await vi.advanceTimersByTimeAsync(20_000);
    expect(reads).toBe(readsWhenReady);
  });

  it('requests an export and shows it in the list', async () => {
    let requested = false;
    server.use(
      http.get('/api/orders/exports', () => HttpResponse.json(requested ? [aBuildingExport] : [])),
      http.post('/api/orders/exports', () => {
        requested = true;
        return HttpResponse.json(aBuildingExport, { status: 202 });
      }),
    );
    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: 'Export my orders' }));

    expect(await screen.findByRole('cell', { name: 'Being built' })).toBeInTheDocument();
  });

  it('says there are no exports yet', async () => {
    listExports();

    renderPage();

    expect(await screen.findByText('No exports yet.')).toBeInTheDocument();
  });

  it('renders the failure when the list cannot be loaded', async () => {
    server.use(
      http.get('/api/orders/exports', () =>
        HttpResponse.json({ title: 'internal_error', status: 500 }, { status: 500 }),
      ),
    );

    renderPage();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('renders a failed re-read even while the list is still empty', async () => {
    let reads = 0;
    server.use(
      http.get('/api/orders/exports', () => {
        reads += 1;
        return reads === 1
          ? HttpResponse.json([])
          : HttpResponse.json({ title: 'internal_error', status: 500 }, { status: 500 });
      }),
    );
    renderPage();

    // Requesting invalidates the list, and that re-read fails with the empty list still cached.
    await userEvent.click(await screen.findByRole('button', { name: 'Export my orders' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('renders the failure when the request is refused', async () => {
    listExports();
    server.use(
      http.post('/api/orders/exports', () =>
        HttpResponse.json({ title: 'internal_error', status: 500 }, { status: 500 }),
      ),
    );
    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: 'Export my orders' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
