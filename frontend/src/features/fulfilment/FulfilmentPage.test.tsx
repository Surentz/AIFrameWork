import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { anOrderToFulfil, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { FulfilmentPage } from './FulfilmentPage';

function renderPage(): void {
  render(<FulfilmentPage />, { wrapper: withQueryClient() });
}

/** The row for one buyer, so an assertion cannot match a different order's button. */
async function rowFor(buyer: string): Promise<HTMLElement> {
  const cell = await screen.findByRole('cell', { name: buyer });
  // eslint-disable-next-line @typescript-eslint/no-non-null-assertion -- a cell is always in a row
  return cell.closest('tr')!;
}

describe('FulfilmentPage', () => {
  it('lists every buyer\'s waiting orders with who placed them', async () => {
    renderPage();

    expect(within(await rowFor('ada')).getByText('Widget')).toBeInTheDocument();
    expect(within(await rowFor('grace')).getByText('Gadget')).toBeInTheDocument();
  });

  it('asks for confirmation naming the order and buyer before shipping', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Ship Widget to ada' }));

    expect(screen.getByText(/Ship 2 × Widget to ada\?/)).toBeInTheDocument();
  });

  it('ships the order it was confirmed for', async () => {
    const shipped: string[] = [];
    server.use(
      http.post('/api/fulfilment/orders/:id/ship', ({ params }) => {
        shipped.push(String(params.id));
        return HttpResponse.json({
          orderId: String(params.id),
          status: 'Shipped',
          changedAt: '2026-09-02T11:00:00+00:00',
        });
      }),
    );
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Ship Widget to ada' }));
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    await screen.findByRole('button', { name: 'Ship Widget to ada' });
    expect(shipped).toEqual([anOrderToFulfil.id]);
  });

  it('does not ship when the confirmation is cancelled', async () => {
    const shipped: string[] = [];
    server.use(
      http.post('/api/fulfilment/orders/:id/ship', ({ params }) => {
        shipped.push(String(params.id));
        return new HttpResponse(null, { status: 500 });
      }),
    );
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Ship Widget to ada' }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.getByRole('button', { name: 'Ship Widget to ada' })).toBeInTheDocument();
    expect(shipped).toEqual([]);
  });

  it('shows the server\'s refusal when an order has already shipped', async () => {
    server.use(
      http.post('/api/fulfilment/orders/:id/ship', () =>
        HttpResponse.json(
          { title: 'Conflict', status: 409, detail: 'The order has already shipped.' },
          { status: 409 },
        ),
      ),
    );
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Ship Widget to ada' }));
    await user.click(screen.getByRole('button', { name: 'Confirm' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('already shipped');
  });

  it('sends back the cursor the server issued and appends the next, later page', async () => {
    // Scripted on the cursor, as in OrderList.test.tsx: a page asked for without it would be
    // page one again, and grace's order would never appear.
    server.use(
      http.get('/api/fulfilment/orders', ({ request }) => {
        const cursor = new URL(request.url).searchParams.get('cursor');

        if (cursor === null) {
          return HttpResponse.json({ items: [anOrderToFulfil], nextCursor: 'CURSOR-1' });
        }

        if (cursor === 'CURSOR-1') {
          return HttpResponse.json({
            items: [{ ...anOrderToFulfil, id: '2', buyerUsername: 'grace' }],
            nextCursor: null,
          });
        }

        throw new Error(`unexpected cursor: ${cursor}`);
      }),
    );
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Load more' }));

    expect(await rowFor('grace')).toBeInTheDocument();
    expect(await rowFor('ada')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('says so when nothing is waiting', async () => {
    server.use(
      http.get('/api/fulfilment/orders', () => HttpResponse.json({ items: [], nextCursor: null })),
    );

    renderPage();

    expect(await screen.findByText('Nothing waiting to ship.')).toBeInTheDocument();
  });

  it('renders the failure when the queue cannot be loaded', async () => {
    server.use(
      http.get('/api/fulfilment/orders', () =>
        HttpResponse.json(
          { title: 'Forbidden', status: 403, detail: 'Not allowed.' },
          { status: 403 },
        ),
      ),
    );

    renderPage();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
