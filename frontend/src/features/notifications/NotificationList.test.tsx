import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { aNotification, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { NotificationList } from './NotificationList';

function renderList(): void {
  render(
    <MemoryRouter>
      <NotificationList />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('NotificationList', () => {
  it('renders one row per notification', async () => {
    renderList();

    expect(await screen.findByText('Order placed')).toBeInTheDocument();
    expect(screen.getByText('Order shipped')).toBeInTheDocument();
    // The count, not just the presence of both: two findByText calls pass identically if the
    // rows were duplicated by a flattening or key bug across infinite pages.
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
  });

  it('announces the loading state before the feed arrives', async () => {
    renderList();

    expect(screen.getByRole('status')).toHaveTextContent('Loading notifications…');

    expect(await screen.findByText('Order placed')).toBeInTheDocument();
  });

  it('offers Mark read only on the unread rows', async () => {
    // The default handler returns one unread and one already read. A button per row would be
    // two; a button on the read row would be a no-op the user can still click.
    renderList();

    expect(await screen.findByText('Order placed')).toBeInTheDocument();
    // Scoped to the list, so the header's own "Mark all read" is not counted.
    expect(within(screen.getByRole('list')).getAllByRole('button')).toHaveLength(1);
    expect(
      screen.getByRole('button', { name: 'Mark Order placed read' }),
    ).toBeInTheDocument();
  });

  it('deep-links an order notification at its order and a product one at its product', async () => {
    server.use(
      http.get('/api/notifications', () =>
        HttpResponse.json({
          items: [
            aNotification,
            {
              ...aNotification,
              id: '88888888-8888-8888-8888-888888888888',
              kind: 'ProductPriceChanged',
              title: 'Price changed',
              subjectId: '44444444-4444-4444-4444-444444444444',
            },
          ],
          nextCursor: null,
        }),
      ),
    );

    renderList();

    // Each View link is named for its own subject, so match the prefix.
    const links = await screen.findAllByRole('link', { name: /^View / });

    // Mapped rather than indexed: noUncheckedIndexedAccess types links[0] as possibly
    // undefined, and asserting the whole array at once also proves there is no third link.
    expect(links.map((link) => link.getAttribute('href'))).toEqual([
      '/orders/11111111-1111-1111-1111-111111111111',
      '/products/44444444-4444-4444-4444-444444444444',
    ]);
  });

  it('offers no View link when the notification has no subject', async () => {
    server.use(
      http.get('/api/notifications', () =>
        HttpResponse.json({
          items: [{ ...aNotification, subjectId: null }],
          nextCursor: null,
        }),
      ),
    );

    renderList();

    expect(await screen.findByText('Order placed')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /^View / })).not.toBeInTheDocument();
  });

  it('marks a single notification read and refetches the feed', async () => {
    let marked: string | null = null;
    server.use(
      http.post('/api/notifications/:id/read', ({ params }) => {
        marked = String(params.id);
        return HttpResponse.json({ markedCount: 1, unreadCount: 0 });
      }),
      // After the invalidation, the row comes back read - so the button disappears, which is
      // what proves the mutation invalidated the list rather than only answering 200.
      http.get('/api/notifications', () =>
        HttpResponse.json({
          items: [
            marked === null
              ? aNotification
              : { ...aNotification, readAt: '2026-09-02T12:00:00+00:00' },
          ],
          nextCursor: null,
        }),
      ),
    );

    renderList();

    await userEvent.click(await screen.findByRole('button', { name: 'Mark Order placed read' }));

    await screen.findByText('Order placed');
    expect(marked).toBe(aNotification.id);
    expect(screen.queryByRole('button', { name: 'Mark Order placed read' })).not.toBeInTheDocument();
  });

  it('marks everything read from the header', async () => {
    let called = 0;
    server.use(
      http.post('/api/notifications/read-all', () => {
        called += 1;
        return HttpResponse.json({ markedCount: 2, unreadCount: 0 });
      }),
    );

    renderList();

    await screen.findByText('Order placed');
    await userEvent.click(screen.getByRole('button', { name: 'Mark all read' }));

    expect(await screen.findByText('Order placed')).toBeInTheDocument();
    expect(called).toBe(1);
  });

  it('asks the server for unread only when the filter is toggled', async () => {
    const asked: (string | null)[] = [];
    server.use(
      http.get('/api/notifications', ({ request }) => {
        asked.push(new URL(request.url).searchParams.get('unreadOnly'));
        return HttpResponse.json({ items: [aNotification], nextCursor: null });
      }),
    );

    renderList();

    await screen.findByText('Order placed');
    await userEvent.click(screen.getByRole('button', { name: 'Unread only' }));

    // The filter is part of the query key, so the toggle is a separate fetch rather than a
    // client-side filter over a cached page that may not hold every unread row.
    expect(await screen.findByRole('button', { name: 'Show all' })).toBeInTheDocument();
    expect(asked).toEqual([null, 'true']);
  });

  it('renders an empty message when there is nothing in the feed', async () => {
    server.use(
      http.get('/api/notifications', () => HttpResponse.json({ items: [], nextCursor: null })),
    );

    renderList();

    expect(await screen.findByText('No notifications yet.')).toBeInTheDocument();
  });

  it('renders the failure instead of an empty feed when the request fails', async () => {
    server.use(
      http.get('/api/notifications', () =>
        HttpResponse.json(
          { title: 'oops', detail: 'Could not load notifications.' },
          { status: 500 },
        ),
      ),
    );

    renderList();

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not load notifications.');
  });

  it('renders the failure when marking read fails, keeping the rows on screen', async () => {
    server.use(
      http.post('/api/notifications/:id/read', () =>
        HttpResponse.json({ title: 'oops', detail: 'Could not mark it read.' }, { status: 500 }),
      ),
    );

    renderList();

    await userEvent.click(await screen.findByRole('button', { name: 'Mark Order placed read' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not mark it read.');
    expect(screen.getByText('Order placed')).toBeInTheDocument();
  });

  it('renders the failure when mark-all fails', async () => {
    server.use(
      http.post('/api/notifications/read-all', () =>
        HttpResponse.json({ title: 'oops', detail: 'Could not mark them read.' }, { status: 500 }),
      ),
    );

    renderList();

    await screen.findByText('Order placed');
    await userEvent.click(screen.getByRole('button', { name: 'Mark all read' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not mark them read.');
  });

  it('sends back the cursor the server issued and appends the page it returns', async () => {
    server.use(
      http.get('/api/notifications', ({ request }) => {
        const cursor = new URL(request.url).searchParams.get('cursor');

        if (cursor === null) {
          return HttpResponse.json({
            items: [{ ...aNotification, id: '1', title: 'Page one' }],
            nextCursor: 'CURSOR-1',
          });
        }

        if (cursor === 'CURSOR-1') {
          return HttpResponse.json({
            items: [{ ...aNotification, id: '3', title: 'Page two' }],
            nextCursor: null,
          });
        }

        throw new Error(`unexpected cursor: ${cursor}`);
      }),
    );

    renderList();

    await userEvent.click(await screen.findByRole('button', { name: 'Load more' }));

    expect(await screen.findByText('Page two')).toBeInTheDocument();
    // Appended, not replaced.
    expect(screen.getByText('Page one')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });
});
