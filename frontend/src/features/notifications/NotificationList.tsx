import { useState } from 'react';
import { Link } from 'react-router-dom';
import {
  useMarkAllNotificationsRead,
  useMarkNotificationRead,
  useNotifications,
} from './queries';
import type { Notification, NotificationKind } from './types';
import './notifications.css';

/**
 * Where a notification's subject lives, or null when there is nowhere useful to go.
 *
 * A switch over the string union rather than an `if` chain, and with no `default` branch, so
 * that adding a kind to the backend contract is a COMPILE error here: the switch stops being
 * exhaustive, the end of the function becomes reachable, and `noImplicitReturns` rejects it.
 * That is the whole reason the API serializes enums by name (see types.ts).
 *
 * `| undefined` in the return type is that compile-time check being honest about its own reach:
 * it only bites once `schema.d.ts` is regenerated, so a deployed backend that has added a kind
 * while a browser still holds the previous bundle falls out of here as `undefined`. Callers have
 * to collapse that — `<Link to={undefined}>` resolves against the current location, and a "View"
 * that silently goes nowhere is worse than no "View" at all.
 */
function subjectPath(
  kind: NotificationKind,
  subjectId: string | null | undefined,
): string | null | undefined {
  if (subjectId === null || subjectId === undefined) {
    return null;
  }

  switch (kind) {
    case 'OrderPlaced':
    case 'OrderShipped':
    case 'OrderCancelled':
      return `/orders/${subjectId}`;
    case 'ProductPriceChanged':
      return `/products/${subjectId}`;
  }
}

interface NotificationRowProps {
  readonly notification: Notification;
  readonly onMarkRead: (id: string) => void;
  readonly isMarking: boolean;
}

function NotificationRow({
  notification,
  onMarkRead,
  isMarking,
}: NotificationRowProps): React.JSX.Element {
  const isRead = notification.readAt !== null && notification.readAt !== undefined;
  // ?? null collapses the unknown-kind case, so the `target !== null` guard below cannot let an
  // undefined through into <Link to={...}>.
  const target = subjectPath(notification.kind, notification.subjectId) ?? null;

  return (
    <li className={isRead ? 'notifications__item' : 'notifications__item is-unread'}>
      <div className="notifications__body">
        <p className="notifications__title">
          {/* The dot is the non-colour cue, but aria-label is PROHIBITED on an empty span's
              implicit `generic` role and browsers do not expose it - so the word is carried by
              real, visually hidden text instead. */}
          {!isRead && (
            <>
              <span className="notifications__dot" aria-hidden="true" />
              <span className="visually-hidden">Unread</span>
            </>
          )}
          {notification.title}
        </p>
        <p className="notifications__text">{notification.body}</p>
        <p className="notifications__when">
          {new Date(notification.createdAt).toLocaleString()}
          {target !== null && (
            <>
              {' · '}
              {/* Named for its subject: twenty rows of "View" is twenty indistinguishable
                  entries in a screen reader's elements list. */}
              <Link to={target} aria-label={`View ${notification.title}`}>
                View
              </Link>
            </>
          )}
        </p>
      </div>

      {!isRead && (
        <button
          className="btn btn--secondary"
          type="button"
          disabled={isMarking}
          aria-label={`Mark ${notification.title} read`}
          onClick={() => {
            onMarkRead(notification.id);
          }}
        >
          Mark read
        </button>
      )}
    </li>
  );
}

interface NotificationHeaderProps {
  readonly unreadOnly: boolean;
  readonly onToggle: () => void;
  readonly onMarkAll: () => void;
  readonly isMarkingAll: boolean;
}

function Header({
  unreadOnly,
  onToggle,
  onMarkAll,
  isMarkingAll,
}: NotificationHeaderProps): React.JSX.Element {
  return (
    <div className="page-header">
      <div>
        <h1 className="page-title">Notifications</h1>
        <p className="page-subtitle">Newest first.</p>
      </div>
      <div className="notifications__actions">
        <button className="btn btn--secondary" type="button" onClick={onToggle}>
          {unreadOnly ? 'Show all' : 'Unread only'}
        </button>
        <button
          className="btn btn--primary"
          type="button"
          disabled={isMarkingAll}
          onClick={onMarkAll}
        >
          {isMarkingAll && <span className="spinner" aria-hidden="true" />}
          Mark all read
        </button>
      </div>
    </div>
  );
}

export function NotificationList(): React.JSX.Element {
  const [unreadOnly, setUnreadOnly] = useState(false);
  const { data, isPending, error, fetchNextPage, hasNextPage, isFetchingNextPage } =
    useNotifications(unreadOnly);
  const markRead = useMarkNotificationRead();
  const markAll = useMarkAllNotificationsRead();

  const header = (
    <Header
      unreadOnly={unreadOnly}
      onToggle={() => {
        setUnreadOnly((current) => !current);
      }}
      onMarkAll={() => {
        markAll.mutate();
      }}
      isMarkingAll={markAll.isPending}
    />
  );

  if (isPending) {
    return (
      <>
        {header}
        <div className="card notifications__state">
          <span className="spinner" aria-hidden="true" />
          <p role="status">Loading notifications…</p>
        </div>
      </>
    );
  }

  // Keyed off data rather than error, so a failed LATER page leaves the rows already on screen
  // where they are — only a failed first page has nothing to show but the failure. Same shape
  // as OrderList.
  if (data === undefined) {
    return (
      <>
        {header}
        <p className="alert" role="alert">
          {error.message}
        </p>
      </>
    );
  }

  const notifications = data.pages.flatMap((page) => page.items);

  return (
    <>
      {header}

      {notifications.length === 0 ? (
        <div className="card notifications__state">
          <p className="notifications__empty-title">
            {unreadOnly ? 'Nothing unread.' : 'No notifications yet.'}
          </p>
          <p className="notifications__text">
            Placing, shipping or cancelling an order puts one here.
          </p>
        </div>
      ) : (
        <ul className="card notifications__list">
          {notifications.map((notification) => (
            <NotificationRow
              key={notification.id}
              notification={notification}
              // Scoped to the row actually in flight: markRead.isPending alone would grey out
              // every other row's button too, for the whole mutation AND the invalidation it
              // awaits.
              isMarking={markRead.isPending && markRead.variables === notification.id}
              onMarkRead={(id) => {
                markRead.mutate(id);
              }}
            />
          ))}
        </ul>
      )}

      {/* Every query and mutation renders its error state - three of them here. */}
      {error && (
        <p className="alert notifications__error" role="alert">
          {error.message}
        </p>
      )}
      {markRead.error && (
        <p className="alert notifications__error" role="alert">
          {markRead.error.message}
        </p>
      )}
      {markAll.error && (
        <p className="alert notifications__error" role="alert">
          {markAll.error.message}
        </p>
      )}

      {hasNextPage && (
        <div className="notifications__more">
          <button
            className="btn btn--secondary"
            type="button"
            disabled={isFetchingNextPage}
            onClick={() => {
              void fetchNextPage();
            }}
          >
            {isFetchingNextPage && <span className="spinner" aria-hidden="true" />}
            {isFetchingNextPage ? 'Loading more…' : 'Load more'}
          </button>
        </div>
      )}
    </>
  );
}
