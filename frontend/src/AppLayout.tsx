import { Link, NavLink, Outlet, useNavigate } from 'react-router-dom';
import { ErrorPanel } from './components/ErrorPanel';
import { Mark } from './components/Mark';
import { useLogout, useSession } from './features/auth/queries';
import { NotificationBell } from './features/notifications/NotificationBell';
import { useNotificationStream } from './features/notifications/stream';
import './AppLayout.css';

export function AppLayout(): React.JSX.Element {
  const { data: session } = useSession();
  const logout = useLogout();
  const navigate = useNavigate();

  // Here rather than inside NotificationBell: one connection per signed-in session, not one per
  // component that happens to care. The layout is also the only thing guaranteed mounted for the
  // whole session, so the connection's lifetime matches the session's rather than a screen's.
  // Its error is intentionally not rendered - see NotificationStreamState.error.
  useNotificationStream();

  return (
    <div className="shell">
      <header className="shell__bar">
        <Link className="shell__brand" to="/orders">
          <Mark size={28} />
          AI Framework
        </Link>

        <nav className="shell__nav">
          {/* `end`, so that /orders/new and /orders/:id do not also light up "All orders". */}
          <NavLink to="/orders" end>
            All orders
          </NavLink>
          <NavLink to="/orders/new">Place an order</NavLink>
          <NavLink to="/orders/exports">Exports</NavLink>
          {/* `end` for the same reason as above: /products/new and /products/:id must not
              light up "Catalogue". */}
          <NavLink to="/products" end>
            Catalogue
          </NavLink>
          <NavLink to="/account/password">Password</NavLink>
          {/* Cosmetics, not the control: the API refuses a non-administrator with a 403 whether
              or not this renders. Hiding it keeps a page nobody can open out of everyone's nav.
              See ADR 0020. */}
          {session?.role === 'Admin' && <NavLink to="/fulfilment">Fulfilment</NavLink>}
          {session?.role === 'Admin' && <NavLink to="/monitoring">Monitoring</NavLink>}
        </nav>

        <div className="shell__session">
          {/* Inside the layout, so it renders on every signed-in screen and nowhere else - the
              feed is per-user and the query would 401 on /login. */}
          <NotificationBell />
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

      <main className="shell__content">
        <Outlet />
      </main>
    </div>
  );
}
