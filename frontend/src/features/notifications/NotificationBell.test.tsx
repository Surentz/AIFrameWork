import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { NotificationBell } from './NotificationBell';

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

  it('still links to the feed when the count request fails', async () => {
    // A failed badge must not take the header down or hide the way to the feed - the feed
    // renders the error properly itself.
    server.use(
      http.get('/api/notifications/unread-count', () =>
        HttpResponse.json({ title: 'oops', detail: 'No count.' }, { status: 500 }),
      ),
    );

    renderBell();

    expect(await screen.findByRole('link', { name: 'Notifications' })).toHaveAttribute(
      'href',
      '/notifications',
    );
  });
});
