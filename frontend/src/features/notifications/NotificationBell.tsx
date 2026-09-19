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

  // A failed count must not take the header down or hide the way to the feed. The bell still
  // links; it just shows no badge, and the feed itself reports the error properly.
  const label =
    error !== null
      ? 'Notifications'
      : unread === 0
        ? 'Notifications, none unread'
        : `Notifications, ${String(unread)} unread`;

  return (
    <Link className="bell" to="/notifications" aria-label={label} title={label}>
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
