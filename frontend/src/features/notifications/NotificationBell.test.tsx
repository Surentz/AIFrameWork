import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { NotificationBell } from './NotificationBell';
import { notificationKeys } from './queries';

function renderBell(): void {
  render(
    <MemoryRouter>
      <NotificationBell />
    </MemoryRouter>,
    { wrapper: withQueryClient() },
  );
}

describe('NotificationBell', () => {
  it('links to the feed and carries the unread count in its accessible name', async () => {
    renderBell();

    const bell = await screen.findByRole('link', { name: 'Notifications, 1 unread' });
    expect(bell).toHaveAttribute('href', '/notifications');
  });

  it('shows the count as a badge', async () => {
    server.use(
      http.get('/api/notifications/unread-count', () => HttpResponse.json({ unreadCount: 7 })),
    );

    renderBell();

    expect(await screen.findByText('7')).toBeInTheDocument();
  });

  it('caps the badge at 99+ while the accessible name keeps the real number', async () => {
    server.use(
      http.get('/api/notifications/unread-count', () => HttpResponse.json({ unreadCount: 140 })),
    );

    renderBell();

    expect(await screen.findByText('99+')).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Notifications, 140 unread' }),
    ).toBeInTheDocument();
  });

  it('shows no badge when nothing is unread', async () => {
    server.use(
      http.get('/api/notifications/unread-count', () => HttpResponse.json({ unreadCount: 0 })),
    );

    renderBell();

    expect(
      await screen.findByRole('link', { name: 'Notifications, none unread' }),
    ).toBeInTheDocument();
    expect(screen.queryByText('0')).not.toBeInTheDocument();
  });

  it('reads a count the API sent as a string', async () => {
    // The generated type is `number | string` because ASP.NET's AllowReadingFromString means the
    // contract genuinely permits both. A bell that did `unreadCount > 0` on the raw value would
    // compare a string and render "12" as the badge while the name said something else.
    server.use(
      http.get('/api/notifications/unread-count', () => HttpResponse.json({ unreadCount: '12' })),
    );

    renderBell();

    expect(await screen.findByText('12')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Notifications, 12 unread' })).toBeInTheDocument();
  });

  it('still links to the feed when the count request fails, and says the count is unreliable', async () => {
    // A failed badge must not take the header down or hide the way to the feed - but it must
    // not be silent either, or the error is swallowed.
    server.use(
      http.get('/api/notifications/unread-count', () =>
        HttpResponse.json({ title: 'oops', detail: 'No count.' }, { status: 500 }),
      ),
    );

    renderBell();

    const bell = await screen.findByRole('link', {
      name: 'Notifications, count may be out of date',
    });
    expect(bell).toHaveAttribute('href', '/notifications');
    expect(bell).toHaveAttribute('title', expect.stringContaining('No count.'));
  });

  it('keeps a stale count in the accessible name when a later poll fails', async () => {
    // TanStack Query RETAINS the last good data when a refetch fails, so the badge still paints
    // the old number. A name keyed off `error` rather than off `data` would drop that number for
    // screen-reader users while leaving it on screen for everyone else.
    let calls = 0;
    server.use(
      http.get('/api/notifications/unread-count', () => {
        calls += 1;
        return calls === 1
          ? HttpResponse.json({ unreadCount: 3 })
          : HttpResponse.json({ title: 'oops', detail: 'No count.' }, { status: 500 });
      }),
    );

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <NotificationBell />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await screen.findByRole('link', { name: 'Notifications, 3 unread' });
    await client.refetchQueries({ queryKey: notificationKeys.unreadCount() });

    expect(
      await screen.findByRole('link', { name: 'Notifications, 3 unread, count may be out of date' }),
    ).toBeInTheDocument();
    // Still painted, which is the whole reason the name has to keep saying it.
    expect(screen.getByText('3')).toBeInTheDocument();
  });

  it('claims nothing about the count before the first response', async () => {
    // `data` undefined and `error` null on the very first render: "none unread" there would be
    // asserting a fact nothing has fetched yet.
    renderBell();

    expect(screen.getByRole('link', { name: 'Notifications' })).toBeInTheDocument();
    expect(
      await screen.findByRole('link', { name: 'Notifications, 1 unread' }),
    ).toBeInTheDocument();
  });
});
