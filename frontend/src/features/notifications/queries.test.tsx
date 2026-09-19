import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { aNotification, server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { NotificationBell } from './NotificationBell';
import { NotificationList } from './NotificationList';

// Covers the reach of the onSuccess invalidateQueries calls in queries.ts: both mutations
// invalidate notificationKeys.all, the PREFIX, rather than naming the list key alone. Narrowing
// either to notificationKeys.list(...) breaks no test in NotificationList.test.tsx - the feed
// still refetches - and silently leaves the badge showing a count the user has just cleared.
// Mounting the bell and the feed under one shared QueryClient is what makes that observable.
describe('marking read and the unread badge', () => {
  it('refetches the badge after mark-all, not just the feed', async () => {
    let allRead = false;
    server.use(
      http.get('/api/notifications/unread-count', () =>
        HttpResponse.json({ unreadCount: allRead ? 0 : 2 }),
      ),
      http.post('/api/notifications/read-all', () => {
        allRead = true;
        return HttpResponse.json({ markedCount: 2, unreadCount: 0 });
      }),
      http.get('/api/notifications', () =>
        HttpResponse.json({
          items: [
            allRead ? { ...aNotification, readAt: '2026-09-02T12:00:00+00:00' } : aNotification,
          ],
          nextCursor: null,
        }),
      ),
    );

    render(
      <MemoryRouter>
        <NotificationBell />
        <NotificationList />
      </MemoryRouter>,
      { wrapper: withQueryClient() },
    );

    expect(
      await screen.findByRole('link', { name: 'Notifications, 2 unread' }),
    ).toBeInTheDocument();

    await userEvent.click(await screen.findByRole('button', { name: 'Mark all read' }));

    expect(
      await screen.findByRole('link', { name: 'Notifications, none unread' }),
    ).toBeInTheDocument();
  });

  it('refetches the badge after marking one row read', async () => {
    let read = false;
    server.use(
      http.get('/api/notifications/unread-count', () =>
        HttpResponse.json({ unreadCount: read ? 0 : 1 }),
      ),
      http.post('/api/notifications/:id/read', () => {
        read = true;
        return HttpResponse.json({ markedCount: 1, unreadCount: 0 });
      }),
      http.get('/api/notifications', () =>
        HttpResponse.json({
          items: [read ? { ...aNotification, readAt: '2026-09-02T12:00:00+00:00' } : aNotification],
          nextCursor: null,
        }),
      ),
    );

    render(
      <MemoryRouter>
        <NotificationBell />
        <NotificationList />
      </MemoryRouter>,
      { wrapper: withQueryClient() },
    );

    await userEvent.click(await screen.findByRole('button', { name: 'Mark Order placed read' }));

    expect(
      await screen.findByRole('link', { name: 'Notifications, none unread' }),
    ).toBeInTheDocument();
  });
});
