import { useEffect, useRef, useState } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';
import { ErrorPanel } from './components/ErrorPanel';
import { AssistantLink } from './features/assistant/AssistantLink';
import { useLogout, useSession } from './features/auth/queries';
import { NotificationBell } from './features/notifications/NotificationBell';
import { useNotificationStream } from './features/notifications/stream';
import { Sidebar } from './Sidebar';
import './AppLayout.css';

export function AppLayout(): React.JSX.Element {
  const { data: session } = useSession();
  const logout = useLogout();
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const [menuOpen, setMenuOpen] = useState(false);
  const menuButton = useRef<HTMLButtonElement>(null);
  // The assistant is a full-height, full-width page (its own conversation list + chat); every
  // other screen keeps the centred, padded column.
  const fullBleed = pathname === '/assistant' || pathname.startsWith('/assistant/');

  // Here rather than inside NotificationBell: one connection per signed-in session, not one per
  // component that happens to care. The layout is also the only thing guaranteed mounted for the
  // whole session, so the connection's lifetime matches the session's rather than a screen's.
  // Its error is intentionally not rendered - see NotificationStreamState.error.
  useNotificationStream();

  // Escape closes the small-screen menu and gives focus back to the button that opened it.
  useEffect(() => {
    if (!menuOpen) {
      return undefined;
    }
    function closeOnEscape(event: KeyboardEvent): void {
      if (event.key === 'Escape') {
        setMenuOpen(false);
        menuButton.current?.focus();
      }
    }
    document.addEventListener('keydown', closeOnEscape);
    return () => {
      document.removeEventListener('keydown', closeOnEscape);
    };
  }, [menuOpen]);

  return (
    <div className="shell">
      <Sidebar
        role={session?.role}
        open={menuOpen}
        onNavigate={() => {
          setMenuOpen(false);
        }}
      />
      {menuOpen && (
        <button
          type="button"
          className="shell__scrim"
          tabIndex={-1}
          aria-label="Close menu"
          onClick={() => {
            setMenuOpen(false);
          }}
        />
      )}

      <div className="shell__body">
        <header className="shell__bar">
          <button
            ref={menuButton}
            type="button"
            className="shell__icon shell__menu"
            aria-label="Menu"
            aria-controls="app-sidebar"
            aria-expanded={menuOpen}
            onClick={() => {
              setMenuOpen((open) => !open);
            }}
          >
            <svg
              viewBox="0 0 24 24"
              width="20"
              height="20"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.8"
              strokeLinecap="round"
              aria-hidden="true"
            >
              <path d="M4 7h16M4 12h16M4 17h16" />
            </svg>
          </button>

          <div className="shell__session">
            {/* Inside the layout, so it renders on every signed-in screen and nowhere else - the
                feed is per-user and the query would 401 on /login. */}
            <NotificationBell />
            <AssistantLink />
            {/* RequireAuth guards this subtree, so session is set by the time the shell renders -
                but the query can still be refetching, so this stays defensive rather than
                asserting. */}
            {session && <span className="shell__user">{session.displayName}</span>}
            <button
              className="btn btn--secondary"
              type="button"
              disabled={logout.isPending}
              onClick={() => {
                logout.mutate(undefined, { onSuccess: () => void navigate('/login') });
              }}
            >
              {logout.isPending && <span className="spinner" aria-hidden="true" />}
              Sign out
            </button>
          </div>
        </header>

        {logout.error && <ErrorPanel error={logout.error} className="shell__error" />}

        <main className={fullBleed ? 'shell__content shell__content--full' : 'shell__content'}>
          <Outlet />
        </main>
      </div>
    </div>
  );
}
