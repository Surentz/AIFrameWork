import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import {
  createNotificationStream,
  type NotificationStreamFactory,
  type NotificationStreamHandle,
} from '../../api/notificationStream';
import { notificationKeys } from './queries';

export interface NotificationStreamState {
  /**
   * Why the push connection is not open, or null while it is (or is still opening).
   *
   * Exposed rather than discarded, but deliberately NOT rendered as an alert anywhere. Push is
   * best-effort by contract (ADR 0016): the feed is the truth and `useUnreadCount`'s interval
   * still runs, so a closed connection costs latency, not correctness. An alert for a degraded
   * optimization would be noise the reader can do nothing about — and this is the ordinary state
   * in any environment with `Realtime__Enabled` off — the default, and what both test hosts
   * pin. It is on in Development and in the Kubernetes overlay; everywhere else this error is
   * the expected steady state rather than a fault.
   */
  readonly error: Error | null;
}

/**
 * Keeps the badge and the feed current by listening for server pushes.
 *
 * This is the thing that makes a red badge appear on its own. Without it the only wake-up is
 * `useUnreadCount`'s interval, so placing an order shows nothing for up to thirty seconds even
 * though the outbox has written the row within about one — and a `ProductPriceChanged` aimed at
 * someone who took no action has no other trigger at all.
 *
 * A `useEffect` despite frontend/CLAUDE.md's "no useEffect data fetching": this fetches nothing.
 * It is a subscription lifecycle — open on mount, close on unmount — which is exactly what an
 * effect is for. The data still arrives through TanStack Query; the push only says "look again".
 */
export function useNotificationStream(
  // Injected for tests. SignalR speaks WebSockets, which MSW cannot intercept, so the connected
  // path cannot be reached any other way.
  createStream: NotificationStreamFactory = createNotificationStream,
): NotificationStreamState {
  const client = useQueryClient();
  const [error, setError] = useState<Error | null>(null);

  useEffect(() => {
    let disposed = false;

    // Opening the connection can fail SYNCHRONOUSLY as well as by rejecting: SignalR validates
    // the URL and the host environment inside build(). Unguarded, that throw escapes the effect
    // and takes down the whole layout — the bell, the nav and the sign-out button — over an
    // optional live update. Under jsdom it throws every time, because SignalR treats any
    // environment with `process` defined as Node and refuses to resolve a relative URL there,
    // which is what lets the tests below reach this path with the real factory.
    //
    // Turned into a rejection rather than handled on its own, so there is ONE failure route to
    // reason about, and so the state update stays in a promise callback — setting it here in
    // the effect body would cascade a render.
    let handle: NotificationStreamHandle;
    try {
      handle = createStream(() => {
        // Invalidate rather than write the pushed notification into the cache. The payload is
        // the same DTO the feed returns, so patching would work — but the feed is paged and
        // filtered two ways, and placing a row correctly in every cached variant is the kind of
        // bookkeeping that goes wrong silently. One refetch of a small list is cheaper than
        // that being wrong.
        void client.invalidateQueries({ queryKey: notificationKeys.all });
      });
    } catch (reason: unknown) {
      // Normalized here rather than in the rejection handler so the rejection reason is always
      // an Error, whatever the factory threw.
      handle = {
        started: Promise.reject(reason instanceof Error ? reason : new Error(String(reason))),
        stop: () => undefined,
      };
    }

    handle.started.then(
      () => {
        if (!disposed) {
          setError(null);
        }
      },
      (reason: unknown) => {
        // The expected outcome when realtime is off: Program.cs only maps the hub when
        // Realtime__Enabled is set, so negotiate answers 404 and this rejects. Recorded, not
        // thrown, and not shown - see NotificationStreamState.error.
        if (!disposed) {
          setError(reason instanceof Error ? reason : new Error(String(reason)));
        }
      },
    );

    return () => {
      disposed = true;
      handle.stop();
    };
  }, [client, createStream]);

  return { error };
}
