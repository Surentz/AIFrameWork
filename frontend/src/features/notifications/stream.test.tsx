import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import type {
  NotificationStreamFactory,
  NotificationStreamHandle,
} from '../../api/notificationStream';
import { server } from '../../test/handlers';
import { withQueryClient } from '../../test/withQueryClient';
import { NotificationBell } from './NotificationBell';
import { useNotificationStream } from './stream';

/**
 * A stand-in for the SignalR connection. SignalR speaks WebSockets, which MSW cannot intercept,
 * so the connected path is only reachable through the factory seam.
 */
function fakeStream(): {
  readonly factory: NotificationStreamFactory;
  readonly push: () => void;
  readonly stopped: () => number;
} {
  let notify: (() => void) | null = null;
  let stops = 0;

  const factory: NotificationStreamFactory = (onNotification) => {
    notify = () => {
      onNotification({
        id: '99999999-9999-9999-9999-999999999999',
        kind: 'OrderPlaced',
        title: 'Order placed',
        body: 'Your order is in.',
        createdAt: '2026-09-02T10:05:00+00:00',
      });
    };

    const handle: NotificationStreamHandle = {
      started: Promise.resolve(),
      stop: () => {
        stops += 1;
      },
    };
    return handle;
  };

  return {
    factory,
    push: () => {
      if (notify === null) {
        throw new Error('the stream was never opened');
      }
      notify();
    },
    stopped: () => stops,
  };
}

interface HarnessProps {
  /** Built once per test and passed by reference, so the effect does not reopen on every
      render - the hook's dep array holds the factory. */
  readonly factory: NotificationStreamFactory;
}

function Harness({ factory }: HarnessProps): React.JSX.Element {
  useNotificationStream(factory);

  return <NotificationBell />;
}

describe('useNotificationStream', () => {
  it('refreshes the unread badge when the server pushes a notification', async () => {
    // This is the whole point of the feature: the badge moves without the user doing anything
    // and without waiting for the poll interval. The count changes only on a REFETCH, so a
    // push that failed to invalidate would leave it at 1.
    let unread = 1;
    server.use(
      http.get('/api/notifications/unread-count', () => HttpResponse.json({ unreadCount: unread })),
    );

    const stream = fakeStream();
    render(
      <MemoryRouter>
        <Harness factory={stream.factory} />
      </MemoryRouter>,
      { wrapper: withQueryClient() },
    );

    expect(
      await screen.findByRole('link', { name: 'Notifications, 1 unread' }),
    ).toBeInTheDocument();

    unread = 2;
    stream.push();

    expect(
      await screen.findByRole('link', { name: 'Notifications, 2 unread' }),
    ).toBeInTheDocument();
  });

  it('closes the connection when the layout unmounts', () => {
    // A connection per mount that is never closed is a leak the user sees as the app getting
    // slower the longer it is open.
    const stream = fakeStream();
    const { unmount } = render(
      <MemoryRouter>
        <Harness factory={stream.factory} />
      </MemoryRouter>,
      { wrapper: withQueryClient() },
    );

    expect(stream.stopped()).toBe(0);

    unmount();

    expect(stream.stopped()).toBe(1);
  });

  it('leaves the bell working when the connection cannot be opened', async () => {
    // The ordinary case wherever Realtime__Enabled is off: negotiate 404s, this rejects, and
    // the feed carries on over REST. Push is best-effort by contract - ADR 0016.
    const failing: NotificationStreamFactory = () => ({
      started: Promise.reject(new Error('Failed to complete negotiation with the server')),
      stop: () => undefined,
    });

    render(
      <MemoryRouter>
        <Harness factory={failing} />
      </MemoryRouter>,
      { wrapper: withQueryClient() },
    );

    expect(
      await screen.findByRole('link', { name: 'Notifications, 1 unread' }),
    ).toBeInTheDocument();
  });
});
