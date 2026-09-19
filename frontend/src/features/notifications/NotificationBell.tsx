import { Link } from 'react-router-dom';
import { useUnreadCount } from './queries';
import './notifications.css';

/**
 * The bell in the app chrome. Deliberately a plain link to the feed rather than a dropdown
 * panel: the list, its paging and its mark-read actions already exist as a route, and a popover
 * would be a second copy of all of it that has to stay in step.
 */
export function NotificationBell(): React.JSX.Element {
  const { data, error } = useUnreadCount();

  // Number(...) because the generated type is `number | string` — ASP.NET's web JSON defaults
  // set AllowReadingFromString, so the contract genuinely permits both and narrowing it in
  // types.ts to look tidier would be a lie (see frontend/CLAUDE.md).
  const unread = data === undefined ? 0 : Number(data.unreadCount);

  // Keyed off `data`, never off `error`, because the two are not alternatives. A failed poll
  // KEEPS the last good `data`, so a count still painted in the badge has to stay in the
  // accessible name too — dropping it there would take the number away from screen-reader users
  // while leaving it on screen for everyone else. And before the first response `data` is
  // undefined while `error` is still null, where "none unread" would assert a fact nothing has
  // fetched yet.
  const count = data === undefined ? null : unread === 0 ? 'none unread' : `${String(unread)} unread`;

  // A failed count must not take the header down or hide the way to the feed, but it must not
  // vanish either: it is said here, and shown by .bell--stale below.
  const label = ['Notifications', count, error !== null ? 'count may be out of date' : null]
    .filter((part): part is string => part !== null)
    .join(', ');

  return (
    <Link
      className={error !== null ? 'bell bell--stale' : 'bell'}
      to="/notifications"
      aria-label={label}
      title={error !== null ? `${label} — ${error.message}` : label}
    >
      <svg
        className="bell__icon"
        viewBox="0 0 24 24"
        width="20"
        height="20"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9" />
        <path d="M13.73 21a2 2 0 0 1-3.46 0" />
      </svg>

      {unread > 0 && (
        // aria-hidden because the count is already in the link's own accessible name above;
        // without it a screen reader announces the number twice.
        <span className="bell__badge" aria-hidden="true">
          {unread > 99 ? '99+' : unread}
        </span>
      )}
    </Link>
  );
}
