import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { delay, http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { aBuildingExport, aPdf, anOrderExport, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { OrderExportsPage } from './OrderExportsPage';

// react-pdf is stubbed: pdf.js cannot draw in jsdom. This is a rendering library, not the API
// client the react-testing skill forbids mocking - the request still goes through MSW. Real
// drawing is Playwright's job (e2e/specs/orders/exports.spec.ts).
const received = vi.hoisted(() => [] as { data: Uint8Array }[]);
vi.mock('react-pdf', () => ({
  pdfjs: { GlobalWorkerOptions: {} },
  Document: ({ file, children, onLoadSuccess }: {
    file: { data: Uint8Array };
    children: React.ReactNode;
    onLoadSuccess?: (pdf: { numPages: number }) => void;
  }) => {
    received.push(file);
    queueMicrotask(() => onLoadSuccess?.({ numPages: 2 }));
    return <div data-testid="pdf-document">{children}</div>;
  },
  Page: ({ pageNumber }: { pageNumber: number }) => <div>{`pdf page ${String(pageNumber)}`}</div>,
}));
vi.mock('react-pdf/dist/Page/TextLayer.css', () => ({}));

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

  it('offers to view a ready export', async () => {
    renderPage();

    expect(await screen.findByRole('button', { name: /^View export requested / })).toBeInTheDocument();
  });

  it('opens the export in a viewer, page by page', async () => {
    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: /^View export requested / }));

    const dialog = await screen.findByRole('dialog', { name: /^Export requested / });
    expect(await within(dialog).findByText('pdf page 2')).toBeInTheDocument();
    expect(within(dialog).getByText('Page 1 of 2')).toBeInTheDocument();
    expect(within(dialog).getByRole('link', { name: 'Download' }))
      .toHaveAttribute('href', `/api/orders/exports/${anOrderExport.id}/download`);
  });

  it('says it is loading while the file is fetched', async () => {
    server.use(http.get('/api/orders/exports/:id/download', async () => {
      await delay('infinite');
      return HttpResponse.arrayBuffer(new ArrayBuffer(0));
    }));
    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: /^View export requested / }));

    expect(await screen.findByText('Loading export…')).toBeInTheDocument();
  });

  it('renders the failure when the file cannot be fetched', async () => {
    server.use(http.get('/api/orders/exports/:id/download', () =>
      HttpResponse.json({ title: 'Not found', detail: 'That export does not exist or is not ready.' }, { status: 404 })));
    renderPage();

    await userEvent.click(await screen.findByRole('button', { name: /^View export requested / }));

    expect(await screen.findByText('That export does not exist or is not ready.')).toBeInTheDocument();
  });

  it('closes the viewer', async () => {
    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: /^View export requested / }));
    const dialog = await screen.findByRole('dialog', { name: /^Export requested / });

    await userEvent.click(within(dialog).getByRole('button', { name: 'Close' }));

    // By name: a closed <dialog> may stay in jsdom's tree, but its title goes with its content.
    await waitFor(() => { expect(screen.queryByRole('dialog', { name: /^Export requested / })).not.toBeInTheDocument(); });
  });

  // Review Focus 4: pdf.js detaches the buffer it is given, so each open must hand it a fresh copy.
  it('gives the viewer a fresh copy of the file every time it opens', async () => {
    received.length = 0;
    renderPage();
    const view = await screen.findByRole('button', { name: /^View export requested / });

    await userEvent.click(view);
    await userEvent.click(
      within(await screen.findByRole('dialog', { name: /^Export requested / })).getByRole('button', { name: 'Close' }),
    );
    await userEvent.click(view);
    await screen.findByRole('dialog', { name: /^Export requested / });

    await waitFor(() => { expect(received.length).toBeGreaterThanOrEqual(2); });
    expect(received[0]?.data).not.toBe(received.at(-1)?.data);
    expect(Array.from(received.at(-1)?.data ?? [])).toEqual(Array.from(aPdf));
  });

  it('describes the export as a PDF to share', async () => {
    renderPage();

    expect(await screen.findByText(/^A PDF of every order you have placed, ready to share\./)).toBeInTheDocument();
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
