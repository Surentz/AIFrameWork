import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import type { Notification } from '../features/notifications/types';

/**
 * The method name the server invokes on the client.
 *
 * Must match `NotificationHub.NotificationReceived` in `src/Api/Notifications/NotificationHub.cs`
 * exactly. A typo on either side produces a message nobody is listening for, with no error on
 * either end — which is why that side holds it as a `const` too rather than a literal.
 */
export const NotificationReceived = 'notificationReceived';

/** The hub's route, as `Program.cs` maps it. Proxied to the API by `vite.config.ts` in dev. */
const HubPath = '/hubs/notifications';

export interface NotificationStreamHandle {
  /** Resolves when connected, rejects when the connection could not be opened. */
  readonly started: Promise<void>;
  /** Closes the connection. Safe to call whether or not `started` resolved. */
  readonly stop: () => void;
}

/**
 * Opens the push connection and calls `onNotification` for each notification pushed to this user.
 *
 * A named type rather than an inline signature because it is the seam the hook's tests inject
 * through: SignalR speaks WebSockets, which MSW cannot intercept, so a fake stream is the only
 * way to exercise the connected path. The 404 path needs no fake — with `Realtime__Enabled`
 * off the server genuinely does not map the hub, which is what
 * `NotificationHubTests.Negotiate_WhenRealtimeIsOff_IsNotMappedAtAll` pins.
 */
export type NotificationStreamFactory = (
  onNotification: (notification: Notification) => void,
) => NotificationStreamHandle;

export const createNotificationStream: NotificationStreamFactory = (onNotification) => {
  const connection = new HubConnectionBuilder()
    .withUrl(HubPath)
    // The cookie carries the session, so nothing is passed here — but that is also why the
    // connection only works from the same origin the app is served from. In dev that is Vite,
    // which is why /hubs needs a proxy entry beside /api, with ws: true.
    .withAutomaticReconnect()
    // Default is Information, which writes a line to the console for every connect and
    // reconnect. Push is an optimization; its lifecycle is not news.
    .configureLogging(LogLevel.Warning)
    .build();

  connection.on(NotificationReceived, onNotification);

  return {
    started: connection.start(),
    stop: () => {
      // Deliberately not awaited: this runs from an effect cleanup, which cannot be async. The
      // returned promise is voided rather than dropped so no-floating-promises stays satisfied
      // and a rejection cannot become an unhandled rejection.
      void connection.stop().catch(() => {
        // Stopping an already-broken connection throws, and there is nothing to do about it:
        // the component is unmounting either way. Not a swallowed failure in the sense
        // frontend/CLAUDE.md forbids — there is no state left to report it to.
      });
    },
  };
};
